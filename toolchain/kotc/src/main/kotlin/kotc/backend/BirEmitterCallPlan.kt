package kotc.backend

import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.types.IrType

// THE CALL-EVALUATION PLAN (BIR `callEval` / `bindRef`; spec docs/bir-cir-spec.md §2.7).
//
// A Kotlin call evaluates its receiver, then each supplied argument, then the callee's omitted defaults — each
// EXACTLY ONCE, whatever number of emitted positions ends up reading it. On the CLR those readers are not one
// position: a filled default splices an earlier value, a reconstructed data-class `copy` field reads the receiver,
// a cross-module `@KotlinDefault` carrier binds `{this}`/`{defaultArgParam n}` to the call's own values. Rendering
// the expression again per reader evaluates it again; hoisting some readers into temps and not others reorders it.
//
// The plan removes the choice. kotc emits ONE ordered list of BINDINGS — the semantic values this call supplies, in
// Kotlin evaluation order — and every reader (the call's own slot, a spliced default, a reconstruction) is a
// `bindRef`, a pure READ that may be cloned freely because cloning a read is not cloning an evaluation. bir2cir's
// CallEvalLowering then decides the physical form ONCE, after every splice has run, and SuspendColdLowering decides
// the storage ONCE, from liveness. No layer may "decline to bind" and fall back to duplicating an expression.
//
// GRANULARITY — a plan is emitted only where a value can acquire a SECOND reader (see [BirEmitter.callNeedsPlan]).
// Without one the positional argument array IS the evaluation plan: one reader per value, positional order = Kotlin
// order. The standing invariant is the converse: any transform that gives a call value a second consumer must go
// through a plan.

/** One call site's ordered evaluation plan, built while its arguments are rendered. Installed for the call node by
 *  [BirEmitter.withCallPlan] and read back by [filledArgs] / [filledExternalArgs]. */
internal class CallPlan(private val e: BirEmitter) {
	private val bindings = ArrayList<String>()
	private val registered = ArrayList<IrExpression>()

	val isEmpty: Boolean get() = bindings.isEmpty()

	/** Append a binding for an ALREADY-RENDERED expression and return the `bindRef` that reads it.
	 *  `phase` is `recv`/`arg`/`default` (documentation of where the value comes from, and what the ordering rule
	 *  below means); `kind` is `value` or `address`; `role` is the source-level phrase a storage diagnostic uses
	 *  instead of the minted id. Append order IS Kotlin evaluation order — the caller is responsible for calling
	 *  this in that order, which is why [filledArgs] renders every supplied value before any filled default. */
	fun bind(phase: String, kind: String, stable: Boolean, type: String, role: String, expr: String): String {
		// `dotkt$b…` NAMESPACE, minted from `scopeCounter` — the counter the emitter's other call-site synthesis uses,
		// distinct from `localSlotCounter` (which renames ordinary locals to `dotkt$localN`) and from `inlCounter`
		// (the `__…` frame names). The three prefixes are disjoint by construction, so no counter has to know the
		// others exist, and `$` is not writable in a plain Kotlin identifier, so a user name cannot alias any of them.
		// This id is the plan's own key, not a local name: bir2cir mints the local a binding lowers to (spec §2.7).
		val id = "dotkt\$b${e.scopeCounter++}"
		bindings.add("""{"id":${e.str(id)},"phase":${e.str(phase)},"kind":${e.str(kind)},"stable":$stable,"type":$type,"role":${e.str(role)},"expr":$expr}""")
		return """{"k":"bindRef","id":${e.str(id)},"sty":$type}"""
	}

	/** Bind an IR value of this call and register the binding as THE rendering of that IR node, so every other reader
	 *  — the call's own receiver/argument slot, an inner-class `new`'s enclosing-instance argument, a spliced default —
	 *  reaches it through the ordinary `expr()` and renders the same read. Re-binding the same node is a no-op. */
	fun bindValue(node: IrExpression, phase: String, role: String): String {
		e.planReads[node]?.let { return it }
		val type = e.birType(node.type).toJson()
		// `expr(node)` FIRST: it may itself install a nested plan, whose bindings belong to that inner call.
		val rendered = e.expr(node)
		val ref = bind(phase, "value", e.isStableValue(node), type, role, rendered)
		e.planReads[node] = ref
		registered.add(node)
		return ref
	}

	/** Drop the node registrations when the plan's scope ends (a nested emission of the same IR node elsewhere is a
	 *  different call site's value). */
	fun release() {
		registered.forEach { e.planReads.remove(it) }
	}

	fun bindingsJson(): String = bindings.joinToString(",", "[", "]")

	/** The call node under its plan — or the bare call when nothing was bound (no plan is emitted then: the positional
	 *  array already IS the evaluation plan). */
	fun wrap(call: String, type: String): String =
		if (bindings.isEmpty()) call
		else """{"k":"callEval","type":$type,"bindings":${bindingsJson()},"expr":$call}"""
}

/** Run `emit` with a fresh evaluation plan installed for `call`, hand back both. The plan is scoped: a nested call
 *  emitted inside `emit` installs its own, and the node registrations are released on the way out. */
internal fun <T> BirEmitter.withCallPlan(call: IrExpression, emit: () -> T): Pair<CallPlan, T> {
	val plan = CallPlan(this)
	val previous = callPlans.put(call, plan)
	val out = try { emit() } finally {
		plan.release()
		if (previous != null) callPlans[call] = previous else callPlans.remove(call)
	}
	return plan to out
}

/** Run `emit` with `callee`'s type frame closed against this call site — the type-level half of the `$default` scope.
 *
 *  EVERY rendering of a callee type into the caller's frame goes through here, because a type written in the callee's
 *  frame names a slot the caller either does not have or has filled with something else. That includes a default whose
 *  EXPRESSION reads nothing at all (its types are still the callee's), and it includes the type a binding is DECLARED
 *  with — reading `p.type` after the scope has been restored is the same bug one line later.
 *
 *  COMPOSES rather than replaces: a default may itself be a call filling a default of its own, and that inner frame
 *  closes against THIS one, which closes against the call site. Inner first, then whatever was already installed, so
 *  at any nesting depth every open type variable ends up closed against the outermost call site. */
internal fun <T> BirEmitter.withDefaultTypeScope(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	callee: org.jetbrains.kotlin.ir.declarations.IrFunction,
	emit: () -> T,
): T {
	val saved = defaultTypeSubst
	val here = callSiteSubstitutor(call, callee)
	defaultTypeSubst = when {
		here == null -> saved
		saved == null -> { t: IrType -> here.substitute(t) }
		else -> { t: IrType -> saved(here.substitute(t)) }
	}
	try { return emit() } finally { defaultTypeSubst = saved }
}

/** The plan of the call currently being emitted. Every path that fills arguments runs inside a plan scope
 *  ([expr] installs one for a function-access node; the two DECLARATION-position call sites install their own), so a
 *  missing plan is an internal invariant break rather than a shape to work around. */
internal fun BirEmitter.callPlan(call: IrExpression): CallPlan =
	callPlans[call] ?: run {
		// Reported as a source-located compile error (which fails the build) rather than thrown: the emitter's own
		// bookkeeping is what is inconsistent, and the detached plan below keeps the rest of this file's emission
		// walking so the message reaches the user with a position.
		invariantBroken(call, "a call filled its arguments outside a call-evaluation plan scope")
		CallPlan(this)
	}
