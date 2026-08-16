package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrDelegatingConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrClassReference
import org.jetbrains.kotlin.ir.expressions.IrEnumConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrExpressionBody
import org.jetbrains.kotlin.ir.declarations.IrEnumEntry
import org.jetbrains.kotlin.ir.expressions.IrGetEnumValue
import org.jetbrains.kotlin.ir.expressions.IrGetField
import org.jetbrains.kotlin.ir.expressions.IrGetObjectValue
import org.jetbrains.kotlin.ir.expressions.IrGetValue
import org.jetbrains.kotlin.ir.expressions.IrInstanceInitializerCall
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrSetField
import org.jetbrains.kotlin.ir.expressions.IrSetValue
import org.jetbrains.kotlin.ir.expressions.IrStringConcatenation
import org.jetbrains.kotlin.ir.expressions.IrThrow
import org.jetbrains.kotlin.ir.expressions.IrTry
import org.jetbrains.kotlin.ir.expressions.IrTypeOperatorCall
import org.jetbrains.kotlin.ir.expressions.IrTypeOperator
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrComposite
import org.jetbrains.kotlin.ir.expressions.IrDoWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrVararg
import org.jetbrains.kotlin.ir.expressions.IrSpreadElement
import org.jetbrains.kotlin.ir.expressions.IrFunctionExpression
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.expressions.IrGetClass
import org.jetbrains.kotlin.ir.declarations.IrLocalDelegatedProperty
import org.jetbrains.kotlin.ir.declarations.IrValueDeclaration
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.declarations.isStaticMethodOfClass
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.util.classId
import org.jetbrains.kotlin.ir.types.classOrNull
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.declarations.IrTypeParameter
import org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.types.isBoxedArray
import org.jetbrains.kotlin.ir.types.isPrimitiveType
import org.jetbrains.kotlin.ir.types.isUnsignedType
import org.jetbrains.kotlin.ir.util.isPrimitiveArray
import org.jetbrains.kotlin.ir.util.isUnsignedArray
import org.jetbrains.kotlin.ir.util.defaultType
import org.jetbrains.kotlin.ir.types.makeNotNull
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

/** The BIR placeholder for an OMITTED default argument this build cannot inline (a cross-module default whose VALUE
 *  the frontend KLIB dropped → IrErrorExpression). Emitted POSITIONALLY so a later provided arg keeps its slot;
 *  bir2cir's DefaultArgSplice replaces it (by array index) from the selected reference DLL. */
private val defaultArgPlaceholder = """{"k":"defaultArg"}"""

/** Regular args, POSITIONALLY complete, filling omitted default arguments (IL has no default-parameter mechanism).
 *  ONE pass for every KOTLIN call shape whose callee IR carries its defaults — a function call, a `new`, an array ctor,
 *  a lifted local/class `new`, a constructor delegation, an enum entry. Reference-KLIB calls whose dependency IR has
 *  no default expression use [filledExternalArgs].
 *  Fill source by default KIND: a same-module CONSTANT/global default is inlined verbatim; a same-module default that
 *  reads the callee's OWN SCOPE — an earlier VALUE PARAMETER (`b: Int = a * 10`, a ctor's `h: Int = w * 2`), the
 *  RECEIVER (`missingDelimiterValue = this`, a data-class `copy`'s `y = this.y`), or an ENCLOSING instance
 *  (`inner class In(val x: Int = outerProp)`) — is inlined with each such read bound BY SYMBOL to THIS call's
 *  expression for it (the JVM `$default` scope, done at the JSON level);
 *  a CROSS-MODULE default (IrErrorExpression — the frontend artifact preserves no default VALUE) becomes a
 *  `defaultArg` placeholder. bir2cir reads the selected reference DLL and produces the complete physical CIR argument
 *  vector; ilemit never reconstructs default semantics.
 *
 *  EVALUATION (docs/bir-cir-spec.md §2.7): a fill can give one of this call's values a SECOND reader — a same-module
 *  default splices it, a reconstructed data-class `copy` field reads the receiver, a cross-module `@KotlinDefault`
 *  carrier binds it. Where that is possible this pass builds the call's EVALUATION PLAN: every value the call supplies
 *  becomes an ordered BINDING (receivers, then the supplied arguments, then the filled defaults in declaration order —
 *  Kotlin's order), and every reader is a `bindRef` READ of it. Where it is not possible no plan is emitted and the
 *  positional array below IS the evaluation plan. bir2cir's CallEvalLowering turns the plan into locals;
 *  SuspendColdLowering decides their storage. Nothing here decides whether a value CAN be held. */
internal fun BirEmitter.filledArgs(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
): List<String> {
	val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
	val receiverParams = callee.parameters.filter {
		it.kind == IrParameterKind.DispatchReceiver || it.kind == IrParameterKind.ExtensionReceiver
	}
	val receiverSyms = receiverParams.map { it.symbol }.toHashSet()
	// Every POSITIONAL parameter ([isValueParameter]: contexts then regulars) — a default reading any of them is bound
	// to this call's argument in that slot. A CONTEXT parameter belongs here for the same reason a regular one does: it
	// is an ordinary argument of this call, so `b: Int = a + c.n` substitutes both `a` and `c` from the emitted args.
	val valueSyms = callee.parameters.filter { isValueParameter(it) }.map { it.symbol }.toHashSet()
	// A default may also read an ENCLOSING instance (`inner class In(val x: Int = outerProp)`, or a member of an inner
	// class). That read is the enclosing class's own `thisReceiver` — NOT one of the callee's parameters — so it is
	// bound separately (see [enclosingThisChain] / [enclosingThisSubst]).
	val enclosingThis = enclosingThisChain(callee)
	val enclosingSyms = enclosingThis.map { it.first.symbol }.toHashSet()
	// The callee's POSITIONAL parameters with their `call.arguments` index — contexts then regulars, the SAME sequence
	// [paramsJsonList] emits as `params` and [overloadSigField] keys `sig` by, so the returned list is index-for-index
	// the declaration's arg array (the caller prepends `__self` on both sides).
	val vals = callee.parameters.mapIndexedNotNull { i, p -> if (isValueParameter(p)) i to p else null }
	val provided = vals.map { (i, _) -> if (i < call.arguments.size) call.arguments[i] else null }
	// The SAME-MODULE defaults this call actually fills. A CROSS-MODULE omission's value is an IrErrorExpression the
	// frontend artifact dropped: nothing of this call is spliced into it HERE (bir2cir's carrier splice does that).
	val filledDefaults = vals.mapIndexed { idx, pair ->
		if (provided[idx] != null) null
		else pair.second.defaultValue?.expression?.takeIf { it !is org.jetbrains.kotlin.ir.expressions.IrErrorExpression }
	}
	// GRANULARITY (§2.7): a plan is emitted only where the positional array below would NOT already be a faithful
	// evaluation plan — i.e. where a value can acquire a SECOND reader, or where the array's ORDER is not Kotlin's:
	//  (a) a same-module default this call fills READS one of the call's values (an earlier parameter, a receiver, an
	//      enclosing instance): the captureSubst channel below splices the value into the default;
	//  (b) a CROSS-MODULE omission: either a `defaultArg` placeholder, whose @KotlinDefault carrier binds
	//      `{defaultArgReceiver kind}` / `{defaultArgParam n}` to THIS call's own values in bir2cir, or a data-class
	//      `copy` field reconstructed as a
	//      read of the receiver. Taken WHOLE (any IrErrorExpression omission) rather than per fill kind, so the test can
	//      never disagree with what the loop below actually emits — a placeholder outside a plan is a loud bir2cir
	//      failure — and a plan whose values all turn out to have one reader costs nothing: CallEvalLowering inlines
	//      every single-reader binding straight back into its slot.
	//  (c) a fill occupies a slot BEFORE a slot this call SUPPLIES. Kotlin evaluates every supplied value before any
	//      default, whatever slots they sit in, so `f(a: Int = mk(), c: Int)` called `f(c = arg())` must run `arg()`
	//      first — while the positional array puts the fill in slot 0. Only a plan can express that, because only a
	//      plan carries an order distinct from the array's.
	// With none of the three, the positional array below IS the evaluation plan: one reader per value, in Kotlin order.
	val lastSupplied = provided.indexOfLast { it != null }
	val planNeeded =
		filledDefaults.any { d ->
			d != null && (refsAny(d, valueSyms) || refsAny(d, receiverSyms) || refsAny(d, enclosingSyms))
		} || vals.indices.any { idx ->
			provided[idx] == null &&
				vals[idx].second.defaultValue?.expression is org.jetbrains.kotlin.ir.expressions.IrErrorExpression
		} || vals.indices.any { idx ->
			idx < lastSupplied && provided[idx] == null && vals[idx].second.defaultValue != null
		}
	val plan = if (planNeeded) callPlan(call) else null
	val label = calleeLabel(callee)
	// The call's RECEIVERS. A default's receiver read binds to the receiver OF ITS OWN KIND: a member EXTENSION has
	// BOTH a dispatch and an extension receiver, so collapsing them to one expression rendered a `this@Owner.k` default
	// as an Owner member read on the EXTENSION receiver's value — a wrong-typed `this` reaching CIL
	// (NullReferenceException at runtime, nothing loud at compile time).
	//   Under a plan both are bound EAGERLY and dispatch-first, because Kotlin evaluates a receiver before every
	// argument and the binding order IS the evaluation order. Without a plan they stay lazy: rendering a receiver has
	// synthesis side effects (a lifted method appended to the file class for a non-capturing lambda, a consumed synth
	// index), so an unread one must not be forced for a rendering that is then discarded. A flattened plain companion
	// has no receiver to bind; a real `object` or companion is bound like anything else — see [needsPlanBinding].
	val dispatchRecv: Lazy<String?> = lazy {
		dispatchReceiver(call)?.let { r ->
			(if (needsPlanBinding(r)) plan else null)?.bindValue(r, "recv", "receiver of '$label'") ?: expr(r)
		}
	}
	val extRecv: Lazy<String?> = lazy {
		extensionReceiver(call)?.let { r ->
			(if (needsPlanBinding(r)) plan else null)?.bindValue(r, "recv", "extension receiver of '$label'") ?: expr(r)
		}
	}
	if (plan != null) {
		if (dispatchReceiver(call)?.let { needsPlanBinding(it) } == true) dispatchRecv.value
		if (extensionReceiver(call)?.let { needsPlanBinding(it) } == true) extRecv.value
	}
	// The instance the ENCLOSING-this chain hangs off: this call's own dispatch receiver — an inner-class member reaches
	// `this@Outer` from its own `this`, and an inner-class `new` takes the enclosing instance AS its dispatch receiver
	// (the leading arg the caller emits, which reads this same value through `expr`).
	val enclosingRecv: String? by lazy { dispatchRecv.value }
	// The emitted JSON per POSITIONAL slot — a `bindRef` under a plan, the rendered expression without one. Null for a
	// slot this pass drops (a purely-trailing uncarried cross-module omission ilemit backfills).
	val slots = arrayOfNulls<String>(vals.size)
	// The filled JSON for each already-processed value parameter — the substitution source for a same-module default
	// that reads ANOTHER value parameter (`b: Int = a * 10`). A Kotlin default may reference only EARLIER params, and
	// every supplied value is processed before any default, so every referenced param is recorded by then.
	val filledByParam = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueParameter, String>()

	// PHASE 1 — every value the call SUPPLIES, in positional order (contexts then regulars). Kotlin evaluates all of
	// them before ANY of the callee's defaults, which is why they are bound first and the defaults follow in phase 2.
	vals.forEachIndexed { idx, pair ->
		val p = pair.second
		val arg = provided[idx] ?: return@forEachIndexed
		// A `byref(x)` / @ClrRefArgument slot takes an ADDRESSABLE lvalue, not a copied value. Under a plan it is an
		// ADDRESS binding: it marks WHERE in the evaluation order the location is computed, and no storage is minted
		// for it — CallEvalLowering pins the impure VALUES the location is computed from at this position and leaves
		// the pure location expression in the slot, so the address is taken at the call and its operands still run
		// where Kotlin runs them.
		val address = addressSlotExpr(arg, p)
		val emitted =
			if (address != null)
				plan?.bind("arg", "address", isStableLocation(arg),
					// The SLOT's type, so the callee's frame — closed here like every other callee type.
					withDefaultTypeScope(call, callee) { birType(p.type).toJson() },
					"argument '${p.name.asString()}' of '$label'", address) ?: address
			else {
				plan?.bindValue(arg, "arg", "argument '${p.name.asString()}' of '$label'")
				argExpr(arg, p)   // the slot's own coercion (nullable unwrap / boxed-Any cast) wraps the bound read
			}
		slots[idx] = emitted
		filledByParam[p] = emitted
	}

	// PHASE 2 — every OMITTED default this call FILLS, in the callee's DECLARATION order (the order the `$default`
	// scope would evaluate them in, and the order a later default's read of an earlier one depends on).
	vals.forEachIndexed { idx, pair ->
		if (provided[idx] != null) return@forEachIndexed
		val p = pair.second
		// An omitted VARARG is filled first: it has no default expression to reach the branch below (Kotlin forbids one),
		// so leaving it to the default fill dropped the slot entirely — see [omittedVararg]. What lands in
		// `filledByParam` is the BINDING's read, exactly like every other fill: a later default naming the vararg
		// (`fun f(vararg xs: Int, y: IntArray = xs)`) splices what is recorded here, and splicing the raw `newArray`
		// would allocate a second empty array for `y` — one Kotlin value, two identities.
		omittedVararg(call, callee, p, plan)?.let { slots[idx] = it; filledByParam[p] = it; return@forEachIndexed }
		val def = p.defaultValue?.expression ?: return@forEachIndexed
		// THE TYPE-FRAME SCOPE ([withDefaultTypeScope]), around EVERY rendering of this default AND the type its
		// binding is declared with — not only the branch that reads the callee's own values. A default's EXPRESSION may
		// read nothing at all and still mention the callee's type parameters in its TYPES
		// (`fun <U> f(x: List<Pair<T, U>> = emptyList())` in a `class C<T>`), and those types belong to the callee's
		// frame whatever the expression does.
		val filled = withDefaultTypeScope(call, callee) {
			fillOmitted(call, callee, plan, p, def,
				valueSyms, receiverSyms, enclosingSyms, receiverParams, enclosingThis, dispatchRecv, extRecv,
				enclosingRecv, filledByParam, label)
		}
		if (filled != null) { slots[idx] = filled; filledByParam[p] = filled }
	}
	return slots.filterNotNull()
}

/** Render ONE omitted default of `call` and, under a plan, bind it — the body of [filledArgs]'s phase 2, extracted so
 *  the type-frame scope wraps it whole. Every type read in here is the CALLEE's, and [BirEmitter.defaultTypeSubst] is
 *  installed for the duration, so a plain `birType` already yields the caller-instantiated form. Null when this slot
 *  emits nothing (a purely-trailing uncarried cross-module omission ilemit backfills). */
private fun BirEmitter.fillOmitted(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	callee: org.jetbrains.kotlin.ir.declarations.IrFunction,
	plan: CallPlan?,
	p: IrValueParameter,
	def: IrExpression,
	valueSyms: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>,
	receiverSyms: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>,
	enclosingSyms: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>,
	receiverParams: List<IrValueParameter>,
	enclosingThis: List<Pair<IrValueParameter, IrClass>>,
	dispatchRecv: Lazy<String?>,
	extRecv: Lazy<String?>,
	enclosingRecv: String?,
	filledByParam: java.util.IdentityHashMap<IrValueParameter, String>,
	label: String,
): String? {
		// Is this fill free to be READ twice — a constant, or the metadata constant a cross-module slot resolves to?
		var stableFill = false
		val emitted: String? = when {
			def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression -> {
				// CROSS-MODULE: the frontend artifact dropped the default VALUE, so a fill can only be RECONSTRUCTED from
				// a Kotlin fact about the callee. [reconstructedDefaultReceiver] owns that decision and names the receiver
				// the reconstruction reads — the receiver this call already bound, so what is read here is that ONE
				// binding and the receiver is evaluated once however many fields are omitted.
				val reconstructed = reconstructedDefaultReceiver(callee, p)?.let { rp ->
					val ext = rp.kind == IrParameterKind.ExtensionReceiver
					val r = if (ext) extensionReceiver(call) else dispatchReceiver(call)
					val recvJson = if (ext) extRecv.value else dispatchRecv.value
					if (r == null || recvJson == null) null
					// Owner via ownerSpec off the RECEIVER EXPRESSION's type (the SAME token the plain `pair.first`
					// property read uses — the referenced, instantiated `kotlin.Pair[Int,Int]`, no `@` this-assembly
					// prefix, no open `gp:` param; the @KotlinDefault splice cannot carry that instantiation). The `sty`
					// stamp is instantiated the same way — see [callSiteType].
					else """{"sty":${birType(p.type).toJson()},"k":"field","ownerType":${ownerSpec(callee.parent as? IrClass, r.type).toJson()},"recv":$recvJson,"name":${str(p.name.asString())}}"""
				}
				// The frontend has already selected a reference-KLIB declaration and admitted the omission. Preserve
				// only that positional fact; bir2cir reads the authoritative KotlinDefault or ECMA-335 constant from
				// the selected reference DLL after overload resolution.
				reconstructed ?: defaultArgPlaceholder
			}
			refsAny(def, valueSyms) || refsAny(def, receiverSyms) || refsAny(def, enclosingSyms) -> {
				// SAME-MODULE default reading the CALLEE'S OWN SCOPE — an earlier VALUE parameter (a CONTEXT
				// parameter, or an earlier regular one: `b: Int = a + c.n`, `b: Int = a * 10`,
				// a ctor's `h: Int = w * 2`), the callee's RECEIVER (`missingDelimiterValue = this`, a data-class
				// `copy`'s `y = this.y`), or an ENCLOSING instance (`inner class In(val x: Int = outerProp)`). Every one
				// of them is bound BY SYMBOL through captureSubst, so each read renders as THIS call's BINDING for that
				// exact value — the `$default` scope, at the emitted-JSON level. Binding by symbol (never a string
				// rewrite of the emitted `{"k":"this"}` token) is what keeps a substituted expression that itself
				// contains `this` — a `c.m(this.k)` argument, or the receiver expression bound for an enclosing
				// instance — from being rewritten a second time into a wrong-receiver call.
				// What gets spliced is a `bindRef`, a pure READ of the one binding, so the value is evaluated exactly
				// once however many defaults read it.
				val subst = ArrayList<Pair<IrValueDeclaration, String>>()
				filledByParam.forEach { (vp, js) -> subst.add(vp to js) }
				// A receiver expression is read ONLY for a default that actually READS it (see the lazies above). So each
				// gate below names exactly the symbol whose value it needs:
				//  - a receiver PARAM binds to the call's receiver of ITS OWN KIND (a member extension has two, and binding
				//    both to one expression reads an owner member off the extension receiver's VALUE);
				//  - the enclosing-`this` chain hangs off `enclosingRecv`, so it is read only for a default that reads an
				//    enclosing instance — never for one that merely reads the extension receiver.
				if (refsAny(def, receiverSyms)) receiverParams.forEach { rp ->
					if (refsAny(def, setOf(rp.symbol)))
						(if (rp.kind == IrParameterKind.ExtensionReceiver) extRecv.value else dispatchRecv.value)
							?.let { subst.add(rp to it) }
				}
				val enclosing = if (refsAny(def, enclosingSyms)) enclosingRecv else null
				subst.addAll(enclosingThisSubst(enclosingThis, enclosing, callee is IrConstructor))
				// Save and RESTORE — a callee parameter can already be a captureSubst key (a closure that captured it,
				// re-entered through a recursive call in its own body); dropping that binding would emit a bare local
				// the closure has no slot for.
				val saved = java.util.IdentityHashMap<IrValueDeclaration, String?>()
				subst.forEach { (d, js) ->
					if (!saved.containsKey(d)) saved[d] = captureSubst[d]
					captureSubst[d] = js
				}
				// ...and the TYPE-level half of the same scope: this default is the CALLEE's IR, so every type it
				// mentions — its own parameter type, the owner of a member it reads off the receiver, a type argument
				// it passes on — is written in the CALLEE's frame. A positional type variable there names a slot the
				// caller's frame does not have: `class G<T>(val v: T) { fun one(a: T = v) }` spliced into a
				// non-generic caller left `G`'s `!0` as the owner of the `v` read (InvalidProgramException at load).
				try { expr(def) }
				finally { saved.forEach { (d, prev) -> if (prev != null) captureSubst[d] = prev else captureSubst.remove(d) } }
			}
			else -> { stableFill = isStableValue(def); argExpr(def, p) }   // constant / global — inline verbatim
		}
		if (emitted == null) return null
		// A filled default is a call-site VALUE like any other: under a plan it becomes a default-phase binding, so a
		// LATER default that reads this parameter reads the ONE binding instead of a second rendering of the expression
		// (`a = bump(), b = a * 10` would otherwise run `bump()` twice).
		return plan?.bind("default", "value", stableFill, birType(p.type).toJson(),
			"default of parameter '${p.name.asString()}'", emitted) ?: emitted
}

/** How a plan binding's ROLE names this callee to a reader: a constructor by its class, anything else by its name. */
internal fun calleeLabel(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
	if (callee is IrConstructor) ((callee.parent as? IrClass)?.name?.asString() ?: "constructor")
	else callee.name.asString()

/** `type` as the CALLER sees it: the DECLARING class's type parameters replaced by this call site's instantiation
 *  (the receiver's type arguments, or a constructor's own constructed type), and the callee's own type parameters by
 *  this call's resolved type ARGUMENTS.
 *
 *  A member declares its signature in its class's type-parameter frame (`kotlin.Triple.copy(first: A, second: B,
 *  third: C)`), so a value reconstructed or bound at the call site must carry the CALL SITE's type (`Int`), not the
 *  positional type variable: an open `tv` in a caller frame that has no type parameters is unresolvable there, and
 *  bir2cir spills it verbatim into a state-machine field when a later argument suspends (`InvalidProgramException` at
 *  the first resume). Identity whenever the frames do not line up — a wrong substitution would be worse than an open
 *  type, which CallEvalLowering can still resolve from the bound value's own static type. */
/** The callee's whole type frame closed against this call site — or null when the frames do not line up.
 *
 *  Installed as [BirEmitter.defaultTypeSubst] for the whole rendering of an omitted default, so EVERY type that
 *  default mentions is closed, not only the ones a caller thought to ask about: the parameter's own type, the owner of
 *  a member it reads off the receiver, a type argument it passes on, the element of a collection it constructs. That
 *  is why installation is unconditional — a default's TYPES belong to the callee's frame however little its
 *  EXPRESSION reads — and why the scopes COMPOSE rather than replace: a default filling a default closes against the
 *  frame it is spliced into, which closes against the call site. */
internal fun BirEmitter.callSiteSubstitutor(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	callee: org.jetbrains.kotlin.ir.declarations.IrFunction,
): org.jetbrains.kotlin.ir.types.IrTypeSubstitutor? {
	val params = ArrayList<IrTypeParameterSymbol>()
	val args = ArrayList<org.jetbrains.kotlin.ir.types.IrTypeArgument>()
	val ownerTps = (callee.parent as? IrClass)?.typeParameters.orEmpty()
	if (callee is IrConstructor) {
		// A Kotlin constructor declares no type parameters of its own, so the call's type ARGUMENTS are exactly its
		// class's — and they ride the call node for EVERY constructor shape. `call.type` would only work for a `new`:
		// a `: super(…)` delegation and an enum-entry call are statements whose type is `Unit`, and reading the owner
		// frame from there left the base class's type variables unsubstituted in the DERIVED class's frame.
		val ta = ownerTps.indices.map { call.typeArguments.getOrNull(it) }
		if (ownerTps.isNotEmpty() && ta.all { it != null }) {
			ownerTps.forEach { params.add(it.symbol) }
			ta.forEach { args.add(it!!) }
		}
	} else {
		if (ownerTps.isNotEmpty()) {
			// A member's owner frame is instantiated by its receiver.
			((dispatchReceiver(call) ?: extensionReceiver(call))?.type as? IrSimpleType)
				?.arguments?.takeIf { it.size == ownerTps.size }?.let { a ->
					ownerTps.forEach { params.add(it.symbol) }
					args.addAll(a)
				}
		}
		val fnTps = callee.typeParameters
		if (fnTps.isNotEmpty()) {
			val ta = fnTps.indices.map { call.typeArguments.getOrNull(it) }
			if (ta.all { it != null }) {
				fnTps.forEach { params.add(it.symbol) }
				ta.forEach { args.add(it!!) }
			}
		}
	}
	if (params.isEmpty()) return null
	return org.jetbrains.kotlin.ir.types.IrTypeSubstitutor(params, args, true)
}

/** The callee-scope RECEIVER parameter whose value [filledArgs] splices when it RECONSTRUCTS the fill for the omitted
 *  parameter `p` — null when no reconstruction applies.
 *
 *  A CROSS-MODULE default reaches this build as an `IrErrorExpression`: the frontend artifact preserves no default
 *  VALUE, so a fill cannot come from the callee's own IR and must come from a Kotlin FACT about the callee. The one
 *  such fact is a data class's SYNTHETIC `copy`, whose omitted field default is `this.<field>` by construction — and the
 *  value that reconstruction reads is the call's receiver. A data class may also declare a differently-signed `copy`
 *  OVERLOAD of its own, whose defaults are ordinary expressions; [isDataClassCopy] tells the two apart by the generated
 *  signature rather than by the name. */
internal fun BirEmitter.reconstructedDefaultReceiver(
	callee: org.jetbrains.kotlin.ir.declarations.IrFunction,
	p: IrValueParameter,
): IrValueParameter? {
	if (p.defaultValue?.expression !is org.jetbrains.kotlin.ir.expressions.IrErrorExpression) return null
	if ((callee as? IrSimpleFunction)?.let { isDataClassCopy(it) } != true) return null
	return callee.parameters.firstOrNull { it.kind == IrParameterKind.DispatchReceiver }
		?: extensionReceiverParam(callee)
}

/** Is this argument's ADDRESS free to be taken twice, and free to move past another value? True for a plain
 *  local/parameter lvalue (`byref(x)`), whose address expression has no side effect and cannot be observed out of
 *  order. Any computed lvalue (`byref(mk().field)`) is not, so its plan binding pins the order instead.
 *
 *  A DIFFERENT question from [isStableValue], which is why it is a different predicate rather than a call to that
 *  one. `isStableValue` asks whether the VALUE re-reads to the same thing, so it excludes a `var`; a LOCATION is
 *  stable for a mutable `var` too, because computing `&x` twice yields the same address however often the storage
 *  it names is written. The absence of the immutability clause below is that distinction, not an omission. The
 *  ref-cell clause it does keep is a different exclusion: a CAPTURED `var` lives in a heap cell, so its location is
 *  a field of an expression rather than a slot of this frame. */
internal fun BirEmitter.isStableLocation(arg: IrExpression): Boolean {
	val inner = byrefMarker(arg) ?: arg
	return inner is IrGetValue && !isRefCell(inner.symbol.owner)
}

/** The args of a constructor DELEGATION (`: this(…)` / `: super(…)`) or an enum-entry constructor call, POSITIONALLY
 *  complete: the enclosing instance an INNER-class target takes as its dispatch receiver (the leading arg, mirroring
 *  the `new` path), then the regular args with omitted defaults filled by [filledArgs]. A delegation is an ordinary
 *  omitting call site — `class D(val a: Int, val b: Int = a * 2) { constructor() : this(3) }` omits `b` exactly as
 *  `D(3)` does — so it must not simply drop the missing argument (that shifts every later arg's slot). */
internal fun BirEmitter.delegatedCtorArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> {
	// FILL FIRST: an INNER target's enclosing instance is this delegation's dispatch receiver, so when the call needs an
	// evaluation plan `filledArgs` binds it and the leading-argument read below renders that ONE binding's `bindRef`.
	val args = filledArgs(call)
	return listOfNotNull(dispatchReceiver(call)?.let { expr(it) }) + args
}

/** The `this` of each class ENCLOSING the callee's own class, innermost FIRST — the enclosing instances a default can
 *  read (`inner class In(val x: Int = outerProp)`, or a member of an inner class). Each is paired with the INNER class
 *  whose `__outer` capture field reaches it. Empty unless the callee's class is `inner`: without an enclosing instance
 *  there is nothing to read. */
internal fun enclosingThisChain(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): List<Pair<IrValueParameter, IrClass>> {
	var cls = callee.parent as? IrClass ?: return emptyList()
	val out = ArrayList<Pair<IrValueParameter, IrClass>>()
	while (cls.isInner) {
		val outer = cls.parent as? IrClass ?: break
		outer.thisReceiver?.let { out.add(it to cls) }
		cls = outer
	}
	return out
}

/** Each enclosing `this` of [chain] bound to the call-site expression that yields it. A CONSTRUCTOR's dispatch
 *  receiver IS the enclosing instance (the `new`'s leading arg), so its first level needs no hop; a MEMBER's receiver
 *  is an instance of the callee's own class, so every level is reached through that class's `__outer` capture field —
 *  the same chain `innerClassDef` installs while emitting the class body. Empty when the call has no receiver
 *  expression to start from. */
internal fun BirEmitter.enclosingThisSubst(
	chain: List<Pair<IrValueParameter, IrClass>>, recv: String?, calleeIsCtor: Boolean,
): List<Pair<IrValueDeclaration, String>> {
	var value = recv ?: return emptyList()
	var hop = !calleeIsCtor
	val out = ArrayList<Pair<IrValueDeclaration, String>>()
	for ((t, inner) in chain) {
		if (hop) value = """{"k":"field","ownerType":${fqnJson(typeName(inner))},"recv":$value,"name":"__outer"}"""
		hop = true
		out.add(t to value)
	}
	return out
}

/** Emit the regular arguments of a resolved reference-KLIB call. A missing dependency-IR default becomes a
 * positional `defaultArg` binding; bir2cir resolves its value from the selected reference DLL. The evaluation plan
 * gives a carried default only bindRef reads of the receiver and supplied arguments, preserving Kotlin evaluation
 * order and exactly-once evaluation. */
internal fun BirEmitter.filledExternalArgs(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	dropPhantomExtensionReceiver: Boolean = false,
): List<String> {
	val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
	// A projected companion extension's lazy declaration retains an extension-receiver parameter that is absent from
	// the call's argument coordinate space. Remove that one semantic-only slot when mapping declaration positions to
	// call positions; this is selected only from the explicit companion-extension fact at the caller.
	val phantomReceiverIndex = if (dropPhantomExtensionReceiver)
		callee.parameters.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
	else -1
	fun callIndex(declarationIndex: Int): Int =
		if (phantomReceiverIndex >= 0 && declarationIndex > phantomReceiverIndex) declarationIndex - 1
		else declarationIndex
	// Every default of a reference-KLIB callee arrives without a usable dependency-IR value,
	// so any omission this pass fills is a cross-module one — see [filledArgs]'s granularity note for why the test is
	// taken whole rather than per fill kind.
	val planNeeded = callee.parameters.withIndex().any { (i, p) ->
		val ai = callIndex(i)
		isValueParameter(p) && (ai >= call.arguments.size || call.arguments[ai] == null) &&
			p.defaultValue?.expression is org.jetbrains.kotlin.ir.expressions.IrErrorExpression
	}
	val plan = if (planNeeded) callPlan(call) else null
	val label = calleeLabel(callee)
	// The receivers, bound FIRST and dispatch-first — Kotlin evaluates them before every argument, and the binding
	// order IS the evaluation order. The caller reads them back through the ordinary `expr()`. A receiver naming an
	// object this backend gives NO INSTANCE is not bound (see [needsPlanBinding]) and the caller's `expr()` renders it
	// in place; a real `object` and a lifted companion are bound, because loading their `INSTANCE` is an
	// evaluation whose position matters.
	if (plan != null) {
		dispatchReceiver(call)?.let { if (needsPlanBinding(it)) plan.bindValue(it, "recv", "receiver of '$label'") }
		if (!dropPhantomExtensionReceiver)
			extensionReceiver(call)?.let { if (needsPlanBinding(it)) plan.bindValue(it, "recv", "extension receiver of '$label'") }
	}
	val out = ArrayList<String>()
	// PHASE 1 — the SUPPLIED values, positionally; PHASE 2 below fills the omissions, which Kotlin evaluates after all
	// of them. `slots` keeps each fill in its own position while the two phases run in evaluation order.
	val vals = callee.parameters.withIndex().filter { isValueParameter(it.value) }
	val slots = arrayOfNulls<String>(vals.size)
	vals.forEachIndexed { valIdx, (i, p) ->
		val ai = callIndex(i)
		val arg = (if (ai < call.arguments.size) call.arguments[ai] else null) ?: return@forEachIndexed
		// A `byref(x)` / @ClrRefArgument arg is emitted as its ADDRESSABLE lvalue (ilemit's EmitArg passes it by
		// address); under a plan that is an `address` binding — an ordering marker, never storage. `addressSlotExpr` is
		// null for every ordinary argument, so this is a no-op everywhere else.
		val address = byrefMarker(arg)?.let { inner -> byrefBackingField(inner) ?: expr(inner) }
		slots[valIdx] =
			if (address != null)
				plan?.bind("arg", "address", isStableLocation(arg),
					withDefaultTypeScope(call, callee) { birType(p.type).toJson() },
					"argument '${p.name.asString()}' of '$label'", address) ?: address
			else plan?.bindValue(arg, "arg", "argument '${p.name.asString()}' of '$label'") ?: expr(arg)
	}
	// PHASE 2 — the omitted defaults, in declaration order.
	vals.forEachIndexed { valIdx, (i, p) ->
		val ai = callIndex(i)
		if (ai < call.arguments.size && call.arguments[ai] != null) return@forEachIndexed
		// An omitted VARARG first, for the reason [omittedVararg] states: it carries no default expression, so the
		// branch below would drop its slot and leave the emitted call one argument short of its own declaration. Under
		// a plan it BINDS: every omission this pass fills is cross-module, and a carrier naming the vararg
		// (`{defaultArgParam n}`) is filled by CLONING this slot — a clone of a read, never of an allocation.
		omittedVararg(call, callee, p, plan)?.let { slots[valIdx] = it; return@forEachIndexed }
		val def = p.defaultValue?.expression ?: return@forEachIndexed
		// The same unconditional type-frame scope as [filledArgs], covering the binding TYPE as well as the rendering.
		var stableFill = false
		val bound: String? = withDefaultTypeScope(call, callee) {
			val rendered =
				if (def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression) defaultArgPlaceholder
				else { stableFill = isStableValue(def); expr(def) }
			// The BINDING TYPE is rendered here too, INSIDE the scope. `p.type` is the callee's, so reading it after
			// the restore leaves an external generic callee's own `!!0` open in the consumer's frame — and a binding a
			// later default reads becomes a local, which would then be declared with it.
			plan?.bind("default", "value", stableFill, birType(p.type).toJson(),
				"default of parameter '${p.name.asString()}'", rendered) ?: rendered
		}
		if (bound != null) slots[valIdx] = bound
	}
	slots.forEach { if (it != null) out.add(it) }
	return out
}

/** The value an OMITTED `vararg` parameter supplies: Kotlin's EMPTY ARRAY of the vararg's element type — the same
 *  `newArray` an explicitly empty vararg renders as (see the [IrVararg] arm of `expr`). Null for any other parameter.
 *
 *  A vararg is omissible without being optional: Kotlin forbids it a default expression, so it reaches neither
 *  half of the default fill, and every argument-vector builder that keys omissions on `defaultValue` dropped the
 *  slot outright. What then left the emitter was a call whose argument vector was SHORTER than the declaration it
 *  named — `f()` on `fun f(vararg xs: Int)`, and equally `Console.WriteLine("x")` on the projected .NET
 *  `params object?[]` overload the frontend selects for it. ilemit refuses that call, correctly: an argument vector
 *  is the one thing it may not reconstruct.
 *
 *  BOUND like every other value this call supplies wherever a plan exists (§2.7): the array is ALLOCATED here, and an
 *  allocation is observable through its identity, so a second reader must read the one allocation rather than a second
 *  rendering of this expression. The readers are real — a later default naming the vararg (`fun f(vararg xs: Int,
 *  y: IntArray = xs)`) splices this slot through [filledArgs]'s `filledByParam`, a cross-module carrier clones it for
 *  its `{defaultArgParam n}` token, and a spliced inline body reads it once per occurrence. `stable` is FALSE for the
 *  same reason: an allocation may never be re-rendered. Without a plan the slot is the value's only reader and the
 *  expression stands in place. The binding lands with the FILLS rather than among the supplied arguments, which its
 *  `arg` phase does not claim otherwise: an empty-array allocation reads nothing, so where it sits in the evaluation
 *  order is unobservable — its IDENTITY is what is observable, and that is what the binding preserves.
 *
 *  The element type is rendered inside the callee's TYPE FRAME — `fun <T> f(vararg xs: T)` called `f<String>()` must
 *  fill `Array<String>`, not the callee's open `T` — and so is the binding's declared type, which is the PARAMETER's
 *  (`IntArray`, `Array<out String>`), read in the same scope for the reason [withDefaultTypeScope] states. */
internal fun BirEmitter.omittedVararg(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	callee: org.jetbrains.kotlin.ir.declarations.IrFunction,
	p: IrValueParameter,
	plan: CallPlan?,
): String? {
	val element = p.varargElementType ?: return null
	return withDefaultTypeScope(call, callee) {
		val rendered = """{"k":"newArray","elem":${birType(element).toJson()},"elems":[]}"""
		plan?.bind("arg", "value", false, birType(p.type).toJson(),
			"omitted vararg '${p.name.asString()}' of '${calleeLabel(callee)}'", rendered) ?: rendered
	}
}

/** True if `expr` reads any of `locals` — detects a default-arg expression that references the callee's own
 *  parameters/receiver (e.g. `b = a * 10`, or a data class `copy`'s `this.x`), which [filledArgs] inlines with those
 *  reads rewritten to THIS call's args/receiver instead of verbatim. */
internal fun BirEmitter.refsAny(expr: IrExpression, locals: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>): Boolean {
	var found = false
	expr.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: IrElement) {
			if (found) return
			if (element is IrGetValue && element.symbol in locals) { found = true; return }
			element.acceptChildrenVoid(this)
		}
	})
	return found
}

internal fun BirEmitter.regularArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: emptyList()
	return call.arguments.mapIndexedNotNull { i, a ->
		if (a != null && i < params.size && isValueParameter(params[i])) a else null
	}
}

/** Every receiver gets its own call-evaluation-plan binding.
 *
 *  Loading an object can run its type initializer, and kotc does not choose whether a semantic companion has a CLR
 *  instance. It therefore keeps the receiver evaluation until bir2cir selects the representation and deliberately
 *  preserves or removes it. This is observable evaluation
 *  (docs/dotkt-semantics.md §7a), and Kotlin evaluates the receiver BEFORE every argument, so it needs a binding to
 *  hold that position: without one, `O.f(side())` lets `side()` run first, and if `O`'s initializer throws it must
 *  not have run at all.
 *
 *  Asked at EVERY receiver binding site, ordinary and inline ([filledArgs], [filledExternalArgs], and the three in
 *  BirEmitterInline): the rule is about what the value IS, not about which emitter reached it. */
internal fun BirEmitter.needsPlanBinding(@Suppress("UNUSED_PARAMETER") receiver: IrExpression): Boolean = true

internal fun BirEmitter.dispatchReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
}

/** The callee's ordinary (non-receiver) value parameters, in order. */
internal fun BirEmitter.regularParams(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrValueParameter> =
	callee.parameters.filter { isValueParameter(it) }

internal fun BirEmitter.extensionReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
	val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
	return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
}

/** Same index-by-parameter-kind approach as [dispatchReceiver]/[extensionReceiver], for an `IrPropertyReference`
 *  (which has no callee `IrFunction` of its own — the getter's parameter SHAPE is used to index its `arguments`).
 *  `IrMemberAccessExpression.dispatchReceiver`/`.extensionReceiver` (the convenience getters) are ERROR-level
 *  deprecated in this compiler version, so kotc never calls them directly, here or on an `IrCall`. */
internal fun BirEmitter.propRefDispatchReceiver(node: IrPropertyReference): IrExpression? {
	val params = (node.getter?.owner ?: node.setter?.owner)?.parameters ?: return null
	val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	return if (idx in 0 until node.arguments.size) node.arguments[idx] else null
}

/** Whether an instance call dispatches VIRTUALLY (callvirt) or is a plain non-virtual `call`.
 *  A `super.X()` call (IrCall.superQualifierSymbol != null) MUST be non-virtual: the callee already points at the
 *  RESOLVED super-class slot, so a `callvirt` would re-dispatch by the receiver's runtime type back to the OVERRIDE
 *  and infinite-loop (issue #14). Otherwise virtual iff the callee is open/overriding. */
internal fun isVirtualInstanceCall(call: IrCall, callee: IrSimpleFunction): Boolean =
	call.superQualifierSymbol == null && (callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty())

/**
 * The declared provider owner of a delegated property's resolved `getValue`/`setValue` call.
 *
 * Whether the type came from this module or a referenced assembly does not change the Kotlin call: kotc carries the
 * delegate value's resolved Kotlin type, including its constructed type arguments, and bir2cir decides how that owner
 * is represented on the CLR. This one rule is shared by local, top-level, and member delegated properties. Stdlib
 * convention delegates are handled by their dedicated paths (lazy) or by the resolved top-level extension fallback
 * (Map); the Read(Write)Property interfaces are genuine member providers.
 */
private fun BirEmitter.delegatedProviderOwner(
	delegateType: IrType,
	access: IrCall? = null,
	accessor: IrSimpleFunction? = null,
): TypeNode? {
	// A member property declared in `Host<T>` is accessed through a constructed `Host<String>` receiver. Its
	// backing-field type is still written in Host's declaration frame (`Provider<T>`), so close that frame before
	// carrying the provider owner into the caller. A top-level/local property has no owner frame and stays unchanged.
	val closedType =
		if (access != null && accessor != null)
			callSiteSubstitutor(access, accessor)?.substitute(delegateType) ?: delegateType
		else delegateType
	if (closedType.classifierOrNull?.owner !is IrClass) return null
	val fq = closedType.classFqName?.asString()
	val isPropertyInterface =
		fq == "kotlin.properties.ReadWriteProperty" || fq == "kotlin.properties.ReadOnlyProperty"
	val isStdlibConvention = fq == "kotlin" || fq?.startsWith("kotlin.") == true
	if (!isPropertyInterface && isStdlibConvention) return null
	return birType(closedType)
}

/** The property accessor is only the frontend wrapper around the convention call. Its body carries the operator
 * declaration FIR actually selected (`Provider.getValue/setValue`), whose complete parameter vector is the member
 * descriptor BIR must preserve. */
private fun delegatedOperatorTarget(accessor: IrSimpleFunction): IrSimpleFunction? {
	val statements = (accessor.body as? IrBlockBody)?.statements.orEmpty()
	return statements.mapNotNull { statement ->
		(statement as? IrReturn)?.value as? IrCall ?: statement as? IrCall
	}.singleOrNull()?.symbol?.owner
}

private fun BirEmitter.delegatedOperatorSig(accessor: IrSimpleFunction): String =
	delegatedOperatorTarget(accessor)?.let { overloadSigField(it) } ?: overloadSigField(accessor)

internal fun TypeNode.containsStarProjection(): Boolean = when (this) {
	TypeNode.Star -> true
	is TypeNode.Fqn -> args?.any { it.containsStarProjection() } == true
	is TypeNode.Fn -> ret.containsStarProjection() || params.any { it.containsStarProjection() } ||
		recv?.containsStarProjection() == true || ctx.any { it.containsStarProjection() }
	is TypeNode.Nullable -> of.containsStarProjection()
	is TypeNode.Oblivious -> of.containsStarProjection()
	is TypeNode.Array -> elem.containsStarProjection()
	is TypeNode.ByRef -> of.containsStarProjection()
	is TypeNode.Tv -> false
}

internal fun BirEmitter.call(call: IrCall): String {
	val rendered = callWithoutDeclarationIdentity(call)
	// A dedicated semantic lowering may deliberately materialize a different physical declaration while retaining
	// the source spelling (rich-enum `values()` is the canonical example). Such a lowering explicitly consumes the
	// frontend selection; do not attach that source declaration's identity to the replacement MethodDef.
	val consumedIdentityMarker = ",\"dotktFrontendDeclarationConsumed\":true"
	if (rendered.endsWith("$consumedIdentityMarker}"))
		return rendered.removeSuffix("$consumedIdentityMarker}") + "}"
	// A fake-override view is not itself emitted. Carry the frontend-resolved real declaration's identity. A
	// dll2klib-projected CLR static appears as a fake override on the semantic class, but its trusted identity
	// annotation is the exact selected MethodDef and there need not be an overridden-symbol edge to resolve. Prefer
	// that carried identity; resolve an ordinary fake override only when the view itself has no physical identity.
	// A local delegated property has no emitted accessor: callWithoutDeclarationIdentity rewrites it to the exact
	// getValue/setValue operator selected by FIR. Use that operator as the semantic target too; attaching the synthetic
	// local accessor's identity would either miss the method-name guard or retarget the generated call incorrectly.
	val selectedTarget = (delegateInlinedAccess?.takeIf { it.first === call }?.second as? IrSimpleFunction)
		?: call.symbol.owner
	// Enum-class API declarations synthesized by FIR are consumed inside callWithoutDeclarationIdentity. Their
	// physical methods are authored later by bir2cir rather than emitted from these IrFunctions, so there is no
	// frontend declaration identity to carry into physical allocation. In particular, rich-enum `values()` keeps
	// the same surface spelling after lowering; comparing only the rendered method name cannot detect consumption.
	val selectedId = declarationIdForPhysicalAllocation(selectedTarget)
	val identityTarget = if (selectedTarget.isFakeOverride && selectedId == null)
		selectedTarget.resolveFakeOverride() ?: return rendered else selectedTarget
	val id = selectedId ?: declarationIdForPhysicalAllocation(identityTarget) ?: return rendered
	if (rendered.startsWith("{\"k\":\"callInline\""))
		return rendered.dropLast(1) + ""","declarationId":${str(id)}}"""
	if (!(rendered.startsWith("{\"k\":\"callStatic\"") ||
			rendered.startsWith("{\"k\":\"callInstance\""))) return rendered
	val semanticMethod = identityTarget.correspondingPropertySymbol?.owner?.name?.asString()
		?: identityTarget.name.asString()
	// Dedicated semantic lowerings may return a different call altogether (`Lazy.getValue` -> `Lazy.value`). The
	// original declaration has already been consumed there and must not retarget the replacement physical member.
	// Inspect only the OUTER node's method field: a receiver or argument can itself contain a call to the semantic
	// method, and a subtree-wide substring test would then attach this declaration's identity to the replacement.
	if (!topLevelJsonStringFieldEquals(rendered, "method", str(semanticMethod))) return rendered
	return rendered.dropLast(1) + ""","declarationId":${str(id)}}"""
}

/** Compare a string-valued field on the root JSON object emitted by this backend without inspecting nested calls.
 * The emitter owns this JSON text and [str] owns value escaping; this scanner only locates the root member boundary. */
private fun topLevelJsonStringFieldEquals(json: String, field: String, encodedValue: String): Boolean {
	val encodedField = "\"$field\""
	var depth = 0
	var index = 0
	while (index < json.length) {
		when (json[index]) {
			'{', '[' -> { depth++; index++ }
			'}', ']' -> { depth--; index++ }
			'"' -> {
				val start = index++
				var escaped = false
				while (index < json.length) {
					val ch = json[index++]
					if (escaped) escaped = false
					else if (ch == '\\') escaped = true
					else if (ch == '"') break
				}
				if (depth != 1 || json.substring(start, index) != encodedField) continue
				while (index < json.length && json[index].isWhitespace()) index++
				if (index >= json.length || json[index++] != ':') return false
				while (index < json.length && json[index].isWhitespace()) index++
				return json.startsWith(encodedValue, index)
			}
			else -> index++
		}
	}
	return false
}

private fun BirEmitter.callWithoutDeclarationIdentity(call: IrCall): String {
	// A `tailrec` self-tail-call -> a back-jump to the method entry (TCO, §2b) instead of a recursive call. Matched
	// by IR identity against the frontend-validated tail-call set installed by `method()`.
	tailrecCtx?.let { ctx -> if (call in ctx.calls) return tailrecJump(call, ctx) }
	val callee = call.symbol.owner
	// A member/top-level delegated property is represented by a real CLR property accessor. Its frontend-generated
	// accessor body already contains the resolved getValue/setValue call, so ordinary call emission is sufficient.
	// `Lazy.getValue` alone is @InlineOnly and has no runtime declaration: lower that call, here in the accessor body,
	// to the real Lazy.value getter. Access sites still call the property getter and never see the delegate implementation.
	if (activeDelegatedAccessor?.backingField?.type?.classFqName?.asString() == "kotlin.Lazy" &&
		callee.fqNameWhenAvailable?.asString() == "kotlin.getValue") {
		val delegate = extensionReceiver(call)
		if (delegate != null) {
			val owner = ownerSpec(delegate.type.classifierOrNull?.owner as? IrClass, delegate.type)
			return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":${expr(delegate)},"method":"value","prop":"get","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, call.type)}}"""
		}
	}
	// NOTE: kotlin.text.MatchResult.value is a REAL interface property (realized by ClrMatchResult) — it must route
	// through the ordinary member-call path, NOT a hardcoded System...Match.Value lowering (that leftover forced the
	// broken MatchResult->Match aliasing above and mis-typed the call).
	// `.message`/`.cause` on a Throwable subclass is a PLAIN Kotlin property read: kotc emits the ordinary
	// semantic getter call (with its `overrides` chain to kotlin.Throwable) below, and bir2cir
	// substitutes it to `clrPropGet System.Exception.Message`/`.InnerException` off the @ClrProperty binding on the
	// ref.dll (kotlin.Throwable is @ClrTypeAlias("System.Exception")). No BCL member name lives in kotc (layer purity).
	// `kotlin.sequences.sequence { yield(…) }` is now ORDINARY library code: it resolves to the real stdlib
	// `sequence(block)` function over the cold core (SequenceBuilderIterator), with `{ yield(...) }` flowing through
	// the ordinary suspend-lambda path (newSuspendLambda -> bir2cir's RestrictedSuspendLambda SM). kotc has NO
	// knowledge of the `sequence`/`yield`/`yieldAll` symbols — the compiler no longer knows the builder exists.
	// `stackBuffer(n) { … }` intrinsic -> scoped stack allocation (splice the block into the caller's frame).
	// Matched by FULL name (`kotlin.clr.stackBuffer`, its CLR-intrinsic home) so a user function happening to be
	// named `stackBuffer` is not mistaken for the intrinsic.
	if (callee.fqNameWhenAvailable?.asString() == "kotlin.clr.stackBuffer")
		return emitStackBuffer(call)
	// #186: a class-DELEGATED ClrEvent<T> property on a Kotlin receiver is still an event handle. Include a fake override
	// inherited by a subclass, but do not capture every local ClrEvent property: field-like `by clrEvent()` and future
	// local implementations have their own provider semantics rather than a delegated-interface receiver.
	fun hasDelegatedEventOrigin(p: IrProperty, seen: MutableSet<IrProperty> = HashSet()): Boolean {
		if (!seen.add(p)) return false
		return p.origin == IrDeclarationOrigin.DELEGATED_MEMBER ||
			p.overriddenSymbols.any { hasDelegatedEventOrigin(it.owner, seen) }
	}
	val localEventProp = callee.correspondingPropertySymbol?.owner
	val localEventOwner = localEventProp?.parent as? IrClass
	if (localEventProp != null && callee === localEventProp.getter
		&& callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent"
		&& hasDelegatedEventOrigin(localEventProp)
		&& localEventOwner != null && !isExternalNetType(localEventOwner)) {
		if (!clrEventReceiverOk) {
			hadError = true
			messageCollector?.report(CompilerMessageSeverity.ERROR,
				"a .NET event ('${localEventProp.name.asString()}') is not a first-class value: it may only be used with " +
					"'.subscribe(handler)', not be read/assigned",
				locationOf(call))
			return """{"k":"unsupportedExpr","of":"clr-event-read-outside-subscription: ${localEventProp.name.asString()}"}"""
		}
		val eventRecv = dispatchReceiver(call)
		if (eventRecv != null)
			return """{"k":"clrEventGet","type":${birType(eventRecv.type).toJson()},"name":${str(localEventProp.name.asString())},"static":false,"recv":${expr(eventRecv)}${overridesJson(callee)}}"""
	}
	// A .NET event subscription `w.Changed.subscribe(h)` resolves (normal Kotlin resolution) to a member of the
	// compiler-owned `kotlin.clr.ClrEvent<T>` fiction (the surfaced form of a .NET event member).
	// kotc emits the PLAIN Kotlin call identity: a
	// `callInstance` on `kotlin.clr.ClrEvent` whose receiver is the event member-access `w.Changed` (a clrEventGet
	// carrying the .NET owner type + event name). NO `add_`/`remove_` naming, NO clrEventAdd here — bir2cir's
	// ClrEventSubscriptionBinding recognizes this node and binds it to the .NET add/remove accessors (the Kotlin<->CLR
	// event relation lives in bir2cir, not kotc). The ClrEvent<T> value is never materialized.
	if (callee.name.asString() == "subscribe"
		&& (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.clr.ClrEvent") {
		val recv = dispatchReceiver(call)!!
		// The receiver here is the ONLY legitimate ClrEvent-value position (the event member-access `w.Changed`);
		// emit it with the OK flag so its clrPropGet is allowed. Every other ClrEvent read stays a compile error.
		val recvJson = asClrEventReceiver { expr(recv) }
		return """{"k":"callInstance","ownerType":${birType(recv.type).toJson()},"virtual":false,"recv":$recvJson,"method":${str(callee.name.asString())},"args":[${expr(regularArgs(call).first())}]}"""
	}
	// RAISE: `handle.invoke(sender, args)` / `handle(sender, args)` (both desugar to `ClrEvent.invoke`). The event handle
	// is a member read `vm.<E>` (a `ClrEvent<T>` property); raise is legal only for a KOTLIN-DECLARED event (one with a
	// synthesized `raise_<E>`). kotc lowers this to a dedicated dialect node `clrEventRaise` carrying the RECEIVER's static
	// type (the type that declares `raise_<E>`) + the event name + the invoke args — bir2cir's ClrEventImplBinding binds it
	// to a `raise_<E>` call (and hard-errors a raise on a CONSUMED foreign event). The ClrEvent<T> value is consumed, never
	// materialized — we emit the underlying receiver `vm`, not the handle read.
	if (callee.name.asString() == "invoke"
		&& (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.clr.ClrEvent") {
		val handle = dispatchReceiver(call)!!
		val eventAccess = handle as? IrCall
		val prop = eventAccess?.symbol?.owner?.correspondingPropertySymbol?.owner
		val eventRecv = eventAccess?.dispatchReceiver
		if (prop == null || eventRecv == null) {
			hadError = true
			messageCollector?.report(CompilerMessageSeverity.ERROR,
				"a .NET event can be raised only through an instance event handle (`vm.<Event>.invoke(...)`)", locationOf(call))
			return """{"k":"unsupportedExpr","of":"clr-event-raise-non-instance-handle"}"""
		}
		// invoke is `vararg args: Any?` — the individual sender/args arrive wrapped in a single IrVararg; unwrap them.
		val rawArgs = regularArgs(call)
		val argExprs = if (rawArgs.size == 1 && rawArgs[0] is IrVararg)
			(rawArgs[0] as IrVararg).elements.filterIsInstance<IrExpression>() else rawArgs
		return """{"k":"clrEventRaise","type":${birType(eventRecv.type).toJson()},"event":${str(prop.name.asString())},"recv":${expr(eventRecv)},"args":[${argExprs.joinToString(",") { expr(it) }}]}"""
	}
	// A `StackBuffer<T>` member access while its block is being spliced -> a stack op (ptr + index).
	((dispatchReceiver(call) as? IrGetValue)?.symbol?.owner)?.let { stackBufSubst[it] }?.let { return emitStackBufferOp(call, callee, it) }
	// A `<get-x>`/`<set-x>` call for a LOCAL delegated property -> access on the delegate local (thisRef=null,
	// no enclosing instance). `by lazy`: the local's `.Value`; custom delegate: getValue/setValue(null, KProperty).
	localDelegates[callee]?.let { ldp ->
		val dvar = ldp.delegate!!
		val dname = localSlotName(dvar)
		val dlocal = """{"k":"local","name":${str(dname)}}"""
		val elem = birType(ldp.getter.returnType)
		// A `ClrRef<T>` delegate (byref local): getValue/setValue inline to ldobj/stobj through the managed pointer.
		if (birType(dvar.type) is TypeNode.ByRef)
			return if (callee === ldp.setter)
				"""{"k":"byrefStore","local":${str(dname)},"elem":${str(elem)},"value":${expr(regularArgs(call).first())}}"""
			else """{"k":"byrefLoad","local":${str(dname)},"elem":${str(elem)}}"""
		// `by lazy` (local): the delegate is a real `kotlin.Lazy<T>` (the stdlib `UnsafeLazyImpl`). Its accessor is
		// the InlineOnly `Lazy<T>.getValue(…) = value` operator, whose stdlib inline body is absent from our IR;
		// inline it (a pure Kotlin-frontend fact) to a plain read of the Lazy interface's `value` getter. bir2cir/
		// ilemit resolve the real emitted `kotlin.Lazy::get_value` — no CLR (System.Lazy) knowledge in kotc.
		if (dvar.type.classFqName?.asString() == "kotlin.Lazy" && callee === ldp.getter) {
			val owner = ownerSpec(dvar.type.classifierOrNull?.owner as? IrClass, dvar.type)
			return delegateInlined(call, """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$dlocal,"method":"value","prop":"get","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, ldp.getter.returnType)}}""")
		}
		// A user delegate class -> its concrete type; a stdlib Read(Write)Property-typed delegate (e.g.
		// `by Delegates.observable(…)`) -> the REAL generic stdlib interface (mirrors `by lazy` on real
		// `kotlin.Lazy<T>`), binding to the actual emitted stdlib getValue/setValue.
		val providerOwner = delegatedProviderOwner(dvar.type)
		val owner = providerOwner?.toJson()
		val ownerGeneric = (providerOwner as? TypeNode.Fqn)?.args != null
		if (owner != null) {
			val kprop = kPropertyStub(ldp.name.asString())
			val nullRef = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
			return delegateInlined(call, if (callee === ldp.setter)
				"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"setValue"${delegatedOperatorSig(callee)},"args":[$nullRef,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"getValue"${delegatedOperatorSig(callee)},"args":[$nullRef,$kprop]${retHint(ownerGeneric, ldp.getter.returnType)}}""",
				delegatedOperatorTarget(callee))
		}
	}
	val name = callee.name.asString()
	val companionExtensionCallTag = companionReceiverCallTag(callee, call)
	val declaringClass = callee.parent as? IrClass
	// A top-level fn has no declaringClass; fall back to the callee's OWN package so an external/user top-level
	// operator (e.g. a restored `operator fun Vec.plus`) isn't mistaken for a kotlin builtin and lowered to a `bin`.
	val isBuiltin = (declaringClass?.fqNameWhenAvailable?.asString() ?: callee.fqNameWhenAvailable?.asString())?.startsWith("kotlin") ?: true
	val pkgFqName = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
	val calleeFq = if (declaringClass == null && pkgFqName != null) "$pkgFqName.$name" else null
	
	// A top-level fun annotated @ClrIntrinsic is NOT bound to a STATIC/INSTANCE .NET call here: that
	// @ClrIntrinsic-driven member-call SUBSTITUTION belongs to bir2cir (sourced from the ref.dll), NOT kotc.
	// kotc emits the PLAIN Kotlin top-level call (the clrStatic file-class path below for external .NET top-level
	// funs is metadata-driven and stays). See [clrName] / CLAUDE.md "kotc reads
	// NEITHER @ClrIntrinsic NOR @ClrTypeAlias".

	// NOTE: collection-interface member routing — `iterator()`/`isEmpty`/`contains`/`containsAll`/`indexOf`/
	// `lastIndexOf`/`subList`/`listIterator()` on a @ClrTypeAlias `kotlin.collections` interface, whose substituted
	// BCL IReadOnly*/IEnumerable face lacks these slots — is OWNED BY bir2cir Rule 5 (Program.cs ~4979), which routes
	// them to the rt `ClrIteratorBridge`/`ClrCollectionDefaults` helpers off the ref.dll @ClrTypeAlias metadata. kotc
	// emits the PLAIN member call (faithful IR); it does NOT name the helper class.

	// A call to a lexical local declaration. The declaration id is the dispatch identity; bir2cir resolves it from
	// lexical scope and only then authors the physical static call and owner.
	localFns[callee]?.let { local ->
		val caps = local.captures
		val capArgs = caps.map { capValueExpr(it) }
		// The lift emits the callee's OWN value params in declaration order (receivers before regulars, see liftLocalFn),
		// so a receiver-bearing local (a local extension fun called as `x.f()`) must pass its dispatch/extension receiver
		// value in that SAME slot, between the captures and the regular args. (A plain local fn has no receiver params →
		// empty → byte-identical to before.)
		// FILL FIRST: under an evaluation plan the fill binds this call's receivers, and the reads below render those
		// bindings rather than a second emission of the receiver expressions.
		val localRegArgs = filledArgs(call)
		val recvArgs = callee.parameters.filter {
			it.kind == IrParameterKind.DispatchReceiver || it.kind == IrParameterKind.ExtensionReceiver
		}.mapNotNull { p ->
			(if (p.kind == IrParameterKind.DispatchReceiver) dispatchReceiver(call) else extensionReceiver(call))?.let { expr(it) }
		}
		val localSig = (caps.map { str(captureFieldType(it)) } +
			callee.parameters.filter { it.kind == IrParameterKind.DispatchReceiver }.map { birType(it.type).toJson() } +
			listOfNotNull(extensionReceiverParam(callee)).map { birType(it.type).toJson() } +
			callee.parameters.filter { isValueParameter(it) }.map { birType(it.type).toJson() })
			.joinToString(",")
		val resolvedTypeArgs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
		val typeArgs = if (local.typeParams.isEmpty()) "" else ""","typeArgs":[${
			localFunctionTypeArgs(local, callee, resolvedTypeArgs).joinToString(",") { it.toJson() }
		}]"""
		// A lift changes only the declaration's location/parameter list; it does not stop being a Kotlin suspend
		// call. Preserve the same call-site fact as every other suspend call so bir2cir can route it to the lowered
		// continuation entry. kotc deliberately does not name that CLR entry here.
		return """{"k":"callLocal","id":${str(local.id)},"sig":[$localSig],"args":[${(capArgs + recvArgs + localRegArgs).joinToString(",")}]$typeArgs${suspendCallTag(callee)}}"""
	}

	// Inlining (lambda-param inline funs only; lambda-less inline = JIT's job — see [[clr-not-jvm-discard-jvmisms]]).
	// An `action(x)` invoke on a lambda param is NO longer special-cased: mechanism-1 is retired (#75 S4b), so an
	// inline fun is a REAL emitted generic method taking a delegate, and `action(x)` inside its body is just the
	// ordinary `callInstance` invoke on that delegate (the fall-through member-call path). bir2cir owns any splice.
	// `(::x)()` (invoking a property-reference VALUE inline) needs NO special handling here: KProperty0/
	// KProperty1's declared `() -> V`/`(T) -> V` supertype gives them a REAL fake-overridden `invoke` abstract
	// member declared directly ON the interface itself (confirmed in the compiled BIR — typeDef's own
	// interfaces-collection drops the FunctionN supertype off ANY interface def, `bt is TypeNode.Fn -> null`,
	// but the fake override still lands in the interface's OWN `methods`). So a call's resolved `declaringClass`
	// for `invoke` on a KProperty0/1-typed receiver is KProperty0/1 itself, never Function0/1 — the ordinary
	// member-call path below emits a plain `callInstance ownerType:kotlin.reflect.KProperty0/1[…] method:invoke`,
	// which `propertyRef`'s lifted class implements directly (mirrors JVM's `PropertyReferenceImpl.invoke() =
	// get()`). bir2cir's CharCodeInvokeLowering only rewrites an `ownerType:kotlin.Function.../KFunction...`
	// call, so it never touches this one.
	// A SAME-MODULE `inline fun` (body present in THIS run) taking ANY lambda arg is source-inlined (AXIS ①): emit the
	// generic `callInline` node and bir2cir splices the raw-BIR body (resolved from `InlineBirStash.Index`) in-context.
	// The lambda ARGS split per-modifier at emit time (AXIS ②, in inlineSpliceCallSameModule): a normal/crossinline
	// lambda -> a spliceable carrier, a noinline lambda -> a real delegate temp. No escape analysis. Gated via
	// `callNeedsSplice` so the suspendCoroutine* intrinsic carve-out is respected here too. (A lambda-less inline call —
	// or a carved-out intrinsic — falls through to the ordinary member-call path.)
	// #87: route inline-splice on the RESOLVED declaration. An INHERITED inline member is a fake override with
	// `body == null`, so a raw `callee.body != null` test would misroute a SAME-module inherited inline call to the
	// cross-module member path below (747). The real declaration carries the body iff it is same-module (kotc holds
	// bodies only for this-run decls; a cross-module base's real decl is also body-less), so routing on `inlineDecl`
	// sends a same-module inherited inline fn to the same-module splice path and a cross-module one to the cross-module
	// path — each matching where emitOwnerfulInlineNode now keys the [KotlinInline] owner (the real declaring class).
	val inlineDecl = callee.let { if (it.isFakeOverride) it.resolveFakeOverride() ?: it else it }
	if (inlineDecl.body != null && callNeedsSplice(call)) return inlineSpliceCallSameModule(call)

	// A property restored from KLIB with Flags.IS_STATIC_PROPERTY is represented by FIR/IR as a static declaration,
	// but a qualified access arrives here through a generated fake-override accessor in Kotlin 2.4. That wrapper has
	// a synthetic dispatch parameter even though the call omits it; for a setter, the value consequently occupies
	// argument slot zero. Resolve that wrapper to the KLIB declaration before asking the standard IR static-member
	// predicate; call-site argument counts are not declaration semantics and would be fragile under new receiver kinds.
	// Preserve the resulting Kotlin fact as a plain static property-accessor call; bir2cir still owns the physical CLR
	// property-vs-field decision off the reference assembly.
	val propertyAccessorDeclaration =
		if (callee.isFakeOverride) callee.resolveFakeOverride() ?: callee else callee
	// Kotlin 2.4 currently drops the static predicate while manufacturing this fake override, but preserves an exact
	// structural invariant: the wrapper declares a synthetic leading dispatch slot which the call omits. A getter is
	// therefore 0/1 and a setter 1/2; an instance property supplies its receiver and is 1/1 or 2/2. This is an IR/KLIB
	// projection fact, not a CLR-shape inference, and is restricted to fake property accessors.
	val wrapperReturnsCompanion =
		(propertyAccessorDeclaration.returnType.classifierOrNull?.owner as? IrClass)?.isCompanion == true
	val klibStaticPropertyWrapper = callee.isFakeOverride && !wrapperReturnsCompanion &&
		callee.parameters.firstOrNull()?.kind == IrParameterKind.DispatchReceiver &&
		call.arguments.size + 1 == callee.parameters.size
	val staticPropertyAccessor = propertyAccessorDeclaration.takeIf {
		(it.isStaticMethodOfClass || klibStaticPropertyWrapper) && it.correspondingPropertySymbol != null
	}
	if (staticPropertyAccessor != null) {
		val staticProperty = staticPropertyAccessor.correspondingPropertySymbol!!.owner
		val staticOwner = staticPropertyAccessor.parent as? IrClass
		if (staticOwner != null) {
			val isSetter = staticPropertyAccessor === staticProperty.setter ||
				callee === callee.correspondingPropertySymbol?.owner?.setter
			// Do not classify by the fake accessor's parameter kinds: its setter value may occupy the synthetic
			// dispatch slot. Every non-null expression is a real static accessor argument in source order.
			val accessorArgs = call.arguments.filterNotNull()
			val argTypes = accessorArgs.joinToString(",") { birType(it.type).toJson() }
			val args = accessorArgs.joinToString(",") { expr(it) }
			val propKind = if (isSetter) "set" else "get"
			val ret = if (isSetter) "" else ""","ret":${birType(call.type).toJson()}"""
			// A property whose storage IS its user-visible member — `const`, `lateinit var`, `@ClrField` — emits no
			// accessor at all (the declaration side gates on the same [fieldRoutedProperty] rule), so its access is
			// the storage itself. Emitting an accessor call here named a `get_`/`set_` slot that does not exist.
			if (!fieldRoutedProperty(staticProperty)) {
				val fieldOwner = fqnJson(typeName(staticOwner))
				val fieldName = str(staticProperty.name.asString())
				return if (isSetter)
					"""{"k":"staticFieldSet","ownerType":$fieldOwner,"name":$fieldName,"value":${accessorArgs.first().let { expr(it) }}}"""
				else if (isLateinitProperty(staticProperty))
					"""{"k":"lateinitGet","ownerType":$fieldOwner,"static":true,"name":$fieldName}"""
				else """{"k":"staticField","ownerType":$fieldOwner,"name":$fieldName}"""
			}
			return """{"k":"callStatic","ownerType":${fqnJson(typeName(staticOwner))},"method":${str(staticProperty.name.asString())},"prop":"$propKind"${overloadSigField(propertyAccessorDeclaration)},"argTypes":[$argTypes]$ret,"args":[$args]}"""
		}
	}

	// `Delegates.observable/vetoable/notNull(…)` is NOT intercepted: it resolves to the REAL stdlib
	// `Delegates.observable`/`vetoable`/`notNull` (emitted into DotKt.Stdlib.dll — each returns a real
	// `ReadWriteProperty<Any?,V>`: an `ObservableProperty` subclass or `NotNullVar`) and flows through the
	// ordinary top-level-call path. The delegate-access sites dispatch getValue/setValue on the real generic
	// interface (see the `by lazy`-parallel routing above). No compiler-synthesized delegate class.
	// `by lazy { … }` is NOT intercepted: the `kotlin.lazy(initializer)` call resolves to the real stdlib
	// `lazy()` actual (returns `UnsafeLazyImpl(initializer)`, a pure-Kotlin `Lazy<T>`) and flows through the
	// ordinary top-level-call path below. No System.Lazy construction here (that is CLR knowledge; layer purity).

	if (name == "compareTo") {
		val recv = dispatchReceiver(call)
		val arg = regularArgs(call).firstOrNull()
		val ec = recv?.type?.classifierOrNull?.owner as? IrClass
		if (recv != null && arg != null && ec?.kind == ClassKind.ENUM_CLASS) {
			fun ord(e: IrExpression): String = if (isRichEnum(ec))
				"""{"k":"field","ownerType":${fqnJson(typeName(ec))},"recv":${expr(e)},"name":"__ordinal"}"""
			else """{"k":"enumOrdinal","e":${expr(e)}}"""
			return """{"k":"binOp","op":"-","lhs":${ord(recv)},"rhs":${ord(arg)}}"""
		}
		// A DIRECT primitive `Double/Float.compareTo(y)` is not special-cased here (Kotlin's TOTAL
		// order — `-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN) == 0` — differs from System.Double.CompareTo). kotc
		// emits the FAITHFUL member call (falls through to the plain callInstance path -> `kotlin.Double.compareTo`)
		// and bir2cir recognizes the Double/Float owner and routes to the stdlib clrDoubleCompare/clrFloatCompare
		// total-order body BEFORE its primitive-compareTo -> System.Double.CompareTo routing. The ENUM branch stays.
	}
	// A PRIMITIVE `x.compareTo(y)` and a `kotlin.Comparable.compareTo` (the `<`/`>`/`<=`/`>=` desugaring on a
	// bounded generic `<T : Comparable<T>>`) are not intercepted here (layer purity): kotc emits the PLAIN
	// member call (`callInstance kotlin.Int.compareTo` / `callInstance kotlin.Comparable.compareTo`, carrying the
	// receiver's static type on the recv node's `retType`/`elem` and the type-param constraints). bir2cir derives the
	// CLR form — a primitive owner -> `clrInstance System.<Prim>.CompareTo`; a @ClrTypeAlias("System.IComparable")
	// owner -> a `constrained.` `System.IComparable<T>::CompareTo` (its ComparableConstrain pass, reusing the
	// value-type/constrained-dispatch knowledge it already owns). The `System.IComparable`/`constrained.` decision
	// is a Kotlin<->CLR relation and lives in bir2cir, not this frontend.

	// NOTE: `reified` gets NO special handling here — it is deliberately never inspected. The CLR has reified
	// generics, so `reified` is pure decoration: a generic function (reified or not) is just emitted as a .NET
	// generic method, and a body that uses `T::class`/`x is T`/`x as T` lowers to `ldtoken !!0`/`isinst !!0`
	// like any other generic-method body. (On the JVM `reified` exists ONLY to drive call-site inlining around
	// erasure; that whole machine is absent here.) See [[clr-not-jvm-discard-jvmisms]].

	// `T::class.simpleName`/`.qualifiedName` is NOT intercepted here (layer purity): kotc emits the PLAIN Kotlin
	// property read on `kotlin.reflect.KClass.simpleName`/`.qualifiedName` (via the ordinary member-property
	// path below), and bir2cir's KClassMemberBinding derives the CLR resolution — a `clrPropGet` on `System.Type`
	// (`Name`/`FullName`). The `System.Type` knowledge (which BCL member a KClass member maps to) is a Kotlin<->CLR
	// relation and lives in bir2cir, not in this frontend.

	// The scope functions (let/run/with/apply/also) and use{} are @kotlin.internal.InlineOnly cross-module
	// inline+lambda funs: they route through the generic owner-less `callInline` node at the external-top-level
	// dispatch below (bir2cir splices their [KotlinInline] raw-BIR payloads off the ref.dll) — NOT special-cased here.

	// `repeat(n) { i -> body }` is NOT special-cased (#75 — the dedicated inlineRepeat splicer is retired). It flows
	// through the general inline gates like any other inline+lambda fn: a LITERAL lambda (AXIS ①) hits the owner-less
	// `callInline` gate below (payload `kotlin.repeat` off the ref.dll — bir2cir wraps the counted loop), and a
	// callable-ref / non-lambda action (`repeat(n, ::fn)` — not an IrFunctionExpression) falls through to the plain
	// `callStatic kotlin.repeat`, which bir2cir's RepeatInlineLowering re-emits as a delegate counter loop.

	// Collection/array factories (`listOf`/`setOf`/`mapOf`/`arrayOf`/`intArrayOf`/`arrayOfNulls`/…) are not
	// recognized here: kotc emits the plain top-level `callStatic kotlin.collections.listOf(...)` (the faithful IR;
	// the vararg argument itself rides as a `newArray` node). bir2cir reads the `@kotlin.clr.ClrCollectionFactory`
	// (kind list/set/map) / `@kotlin.clr.ClrArrayFactory` (vararg/sized) marker off each stdlib factory function on
	// the ref.dll and re-emits the same `{k:newList/newSet/newMap/newArray/newArraySized}` construction node — the
	// element/key/value types from the call's `typeArgs`, the elements from the vararg arg. The `mapOf(a to b)`
	// literal-split (and its "do NOT force-split a non-literal Pair" guard — `mapOf(pairVar)` stays a real call)
	// is bir2cir's.

	// Unsigned<->signed byte-array reinterpret (#76) — `UByteArray.toByteArray()` / `ByteArray.toUByteArray()` — is
	// NOT lowered here: it is a CLR-representation fact ("UByteArray IS byte[]"), so kotc emits the FAITHFUL top-level
	// extension call and bir2cir re-emits the reinterpret `cast` keyed on the resolved receiver identity
	// (FaithfulHintRecognition, M9). The Kotlin<->CLR relation lives there, not in kotc.

	// `e!!` (not-null assertion). Kotlin `x!!` throws NullPointerException IMMEDIATELY when x is null,
	// regardless of how the result is used (stored, discarded, or dereferenced). Both operand kinds bind
	// the operand to a temp ONCE (it may have side effects), null-test it, and throw kotlin.NullPointerException
	// on null; the non-null value is yielded otherwise. A value-type-nullable operand (`Int?` = `Nullable<T>`)
	// tests via HasValue and unwraps .Value — a bare pass-through would leave a `Nullable<T>` STRUCT where the
	// use site consumes the bare value (`n!! + 1` -> InvalidProgram; `n!!.toLong()` reads garbage). A
	// reference-nullable operand tests via objEq-null (mirrors the requireNotNull/checkNotNull reference path
	// in bir2cir's PreconditionLowering) — a bare pass-through would let a null surface only as a later
	// NullReferenceException at a deref (wrong exception type + site) and NEVER throw for a stored/discarded
	// `x!!`. `!!` throws kotlin.NullPointerException; the precondition helpers throw IllegalArgument/State.
	if (name == "CHECK_NOT_NULL") {
		val arg = call.arguments.filterNotNull().first()
		val velem = nullableElem(arg.type)
		val nv = "__nn${scopeCounter++}"
		val nvLoc = """{"k":"local","name":${str(nv)}}"""
		val throwNpe = throwExpr(newExc("kotlin.NullPointerException", null))
		if (velem != null) {
			return valueBlockJson(
				type = null,
				stmts = """{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(velem).toJson()},"init":${expr(arg)}}""",
				result = """{"k":"cond","cond":{"k":"nullableHasValue","elem":${velem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${velem.toJson()},"e":$nvLoc},"else":$throwNpe}""",
			)
		}
		// reference (or objEq-testable: generic `T?`) operand: bind once, `(t != null) ? t : throw` (value in
		// `then`, mirroring the value-type path above and bir2cir's PreconditionLowering reference shape). objEq
		// boxes a generic local before the null-test, so a HasValue==false `Nullable<T>` reads as a genuine null
		// and throws. (Unsigned `UInt?`/`UByte?`/... take the value-type HasValue/Value branch ABOVE: #118 -- they
		// ARE value types on the CLR (`Nullable<uint>`), so `nullableElem` includes them via `isPrimitiveOrUnsigned`;
		// a bare pass-through would leave a `Nullable<uint>` STRUCT at the use site, the #56 struct-consumer issue.)
		val nullConst = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
		return valueBlockJson(
			type = null,
			stmts = """{"k":"var","name":${str(nv)},"type":${birType(arg.type).toJson()},"init":${expr(arg)}}""",
			result = """{"k":"cond","cond":{"k":"unaryOp","op":"!","e":{"k":"objEq","lhs":$nvLoc,"rhs":$nullConst}},"then":$nvLoc,"else":$throwNpe}""",
		)
	}

	// Value-position primitive `rangeTo`/`rangeUntil` (`a..b` / `a..<b`) is NOT lowered here. kotc emits the
	// FAITHFUL `callInstance kotlin.Int.rangeTo(b)` member call (CLR primitives have no instance methods, but that
	// is a CLR fact); bir2cir (RangeConstructionLowering) MATERIALIZES the stdlib range class — `new IntRange/
	// LongRange/CharRange`, applying the `-1` half-open arithmetic for rangeUntil. Structured for-loops are still
	// counter-lowered in birForLoop (they intercept the range at the IR level before this member call is emitted).

	// `x in a..b` (range membership) is NOT lowered here. kotc emits the FAITHFUL `contains` member call on the
	// range receiver (`callInstance <range>.contains(x)`) by identity — NO comparison synthesis and NO FQN gate,
	// so a USER type with `operator fun rangeTo`+`contains` stays a real method dispatch (the bare-name lowering
	// here MISCOMPILED it to primitive comparisons). bir2cir (RangeMembershipLowering) lowers `x in a..b` /
	// `x in a until b` to the short-circuit `(x >= a && x <op> b)` fast path FQN-keyed — only when the range is an
	// un-materialized primitive `kotlin.<Prim>.rangeTo/rangeUntil` — binding the two bounds and the subject ONCE
	// EACH, in that (Kotlin) order, so the short circuit neither skips a bound nor reads the subject early.
	// The Kotlin<->CLR range relation lives in bir2cir.

	// Enum rich API: Color.values()/entries -> Enum.GetValues<T>(); Color.valueOf(s) -> Enum.Parse<T>(s).
	val enumDeclarationOwner = (callee.parent as? IrClass)
		?: (callee.correspondingPropertySymbol?.owner?.parent as? IrClass)
	val enumResultOwner = if (name == "entries" || name == "<get-entries>")
		((call.type as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)
			?.type?.classifierOrNull?.owner as? IrClass
	else null
	val enumApiOwner = (enumDeclarationOwner ?: enumResultOwner)?.let { owner ->
		when {
			owner.kind == ClassKind.ENUM_CLASS -> owner
			owner.isCompanion -> (owner.parent as? IrClass)?.takeIf { it.kind == ClassKind.ENUM_CLASS }
			else -> null
		}
	}
	enumApiOwner?.let { ec ->
		// K2 may expose the synthesized entries getter without a corresponding
		// property symbol after KLIB dependencies are present. Its special IR
		// name still carries the same Kotlin declaration identity.
		val isEntriesGetter =
			callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries" ||
				name == "entries" || name == "<get-entries>"
		// Rich enum -> the synthesized static values()/valueOf() methods on the class.
		if (isRichEnum(ec)) {
			if (name == "values" || isEntriesGetter)
				return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"values","args":[],"dotktFrontendDeclarationConsumed":true}"""
			if (name == "valueOf") return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"valueOf","sig":[${fqnJson("kotlin.String")}],"args":[${expr(regularArgs(call).first())}],"dotktFrontendDeclarationConsumed":true}"""
		}
		// Basic enum -> the semantic enumValues/enumParse node carrying the enum's FAITHFUL FQN identity (a
		// structured Type, never the banned `@Name` type-token). bir2cir/ilemit resolve it to the local enum type,
		// exactly as the reified `enumValues<T>()` path does (EnumIntrinsicLowering re-emits the same node shape).
		if (name == "values" || isEntriesGetter)
			return """{"k":"enumValues","type":${fqnJson(ec.name.asString())}}"""
		if (name == "valueOf") return """{"k":"enumParse","type":${fqnJson(ec.name.asString())},"arg":${expr(regularArgs(call).first())}}"""
	}
	// The top-level reified enum intrinsics `enumValues<T>()` / `enumValueOf<T>(name)` / `enumEntries<T>()`
	// / `enumEntriesIntrinsic<T>()` are NOT recognized here: kotc emits the FAITHFUL top-level call
	// `callStatic owner:null method:<the callee's bare name> typeArgs:[T] args:[…]` (the plain Kotlin fact) via the
	// general call path. bir2cir's EnumIntrinsicLowering re-emits the same BIR vocabulary — a rich enum's synthesized static
	// `values()`/`valueOf()`, or the semantic `enumValues`/`enumParse` node for a basic/generic-param T — deriving
	// rich-vs-basic from the enum type's emitted shape (a local rich enum carries `enumRich:true`). "This call is
	// enumValues" is a Kotlin<->CLR relation, so it lives in bir2cir. (The `.name`/`.ordinal` handling below asks
	// the IR — `ClassKind.ENUM_CLASS` — not an FQN table, so it stays here.)
	// `c.code` (Char -> Int code point) is NOT recognized here: kotc emits the FAITHFUL top-level extension-property
	// getter call `callStatic owner:null method:code prop:get sig:[kotlin.Char] args:[<char>]` via
	// the general property path. bir2cir's CharCodeInvokeLowering re-emits the `{k:conv, to:kotlin.Int}` node (a
	// genuine primitive IL op — the char value AS an int, distinct from `.toInt()`'s @ClrConv) off that faithful
	// call. The Kotlin<->CLR relation lives in bir2cir; no physical accessor name is authored here.
	// c.name -> toString() (enum name); c.ordinal -> (int)c.  Rich enum -> the __name/__ordinal fields.
	dispatchReceiver(call)?.takeIf { (it.type.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS }?.let { rc ->
		val rec = (rc.type.classifierOrNull?.owner as? IrClass)
		if (rec != null && isRichEnum(rec)) when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
			"name" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__name"}"""
			"ordinal" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__ordinal"}"""
		}
		when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
			"name" -> return """{"k":"objMethod","method":"toString","recv":${expr(rc)}}"""
			"ordinal" -> return """{"k":"enumOrdinal","e":${expr(rc)}}"""
		}
	}

	// `a to b` and Pair/Triple/IndexedValue `componentN()` are NOT recognized here: these are real
	// emitted stdlib types with real members — the infix `to` (body `Pair(this, that)`) and the data-class
	// component1()/component2()/component3() operators are materialized IR declarations. kotc emits the plain call
	// (faithful IR) and it resolves against the real stdlib surface; no marker is needed (unlike conv/factories,
	// which synthesize CLR-shaped nodes). So `5 to 6`, `val (a, b) = pair`, `t.component1()` all fall through to
	// the ordinary call path.
	// Map-entry destructuring `entry.component1()/.component2()` is NOT lowered to KeyValuePair.Key/.Value here:
	// map entries are real `kotlin.collections.Map.Entry` objects (rt ClrMutableMapEntry; both Map/MutableMap alias
	// IDictionary), so the destructure components emit as the PLAIN Kotlin extension calls and resolve like any
	// stdlib call. Reading a ref object as a KeyValuePair struct would reinterpret memory -> garbage values (and
	// KeyValuePair is CLR knowledge the layer rules forbid inside kotc).

	// Invoking a function-typed value `f(x)` -> delegate `Invoke` (Func/Action) is NOT recognized here: kotc emits
	// the FAITHFUL `callInstance ownerType:kotlin.FunctionN[..]/kotlin.reflect.KFunctionN[..] method:invoke` member
	// call (the plain Kotlin fact) via the general instance-call path. bir2cir's CharCodeInvokeLowering re-emits the
	// `{k:delegateInvoke}` node off that faithful call — deriving `funcType` from the FunctionN owner's type args
	// (params = args[..n-1], ret = args[n]). A function-typed value IS a delegate at the CLR level; that Kotlin<->CLR
	// relation lives in bir2cir. (Includes a callable-reference value `(c::method)(x)` whose type is `KFunctionN`.)
	// MutableList/MutableCollection mutation members (`add`/`remove`/`clear`/`removeAt`) -> the BCL List<T>
	// instance method. Kotlin collections lower to System.Collections.Generic.List<T>; these are instance calls,
	// not collection extension ops (the real stdlib `map`/`filter`/`mapTo` bodies — which build an ArrayList via
	// `.add(...)` — run on the BCL list).
	// Array indexing `a[i]` / `a[i] = v` (the `get`/`set` operators on Array/primitive arrays).
	if (callee.isOperator && (name == "get" || name == "set")) {
		val recv = dispatchReceiver(call)
		if (recv != null && isArrayType(recv.type)) {
			// No `elem` field: bir2cir DERIVES the element off the array operand's (now faithful) type. kotc emits
			// only the faithful get/set intrinsic + the array operand.
			val a = regularArgs(call)
			return if (name == "get") """{"k":"arrayGet","array":${expr(recv)},"index":${expr(a[0])}}"""
			else """{"k":"arraySet","array":${expr(recv)},"index":${expr(a[0])},"value":${expr(a[1])}}"""
		}
		// String indexing `s[i]` is NOT lowered here: `kotlin.String.get(index)`
		// carries @ClrIntrinsic("get_Chars") (runtime/stdlib/clr/builtins/String.kt); kotc emits the plain operator
		// `get` member call on kotlin.String and bir2cir's MemberCallSubstitution rewrites it to
		// `clrInstance System.String.get_Chars` off the ref.dll — the Kotlin<->CLR relation lives in bir2cir, not kotc.
		// kotlin.* List/Map indexing `list[i]`/`m[k]` is NOT intercepted: in FIR it's already an operator call to
		// `get`/`set` — fall through to the ordinary call path so it emits as a real kotlin.* `get`/`set` call.
		// Projected .NET indexer `c[i]` / `c[i] = v` -> the DEFAULT INDEXED PROPERTY of the constructed .NET type.
		// kotc emits the FAITHFUL Kotlin get/set operator identity (`method:"get"/"set"`) plus an index marker
		// (`"prop":"index-get"/"index-set"`, extending step 3's accessor-KIND mechanism); it does NOT bake the CLR
		// slot name. bir2cir's NetInteropBinding reflects the .NET type's default indexed property off the refs (its
		// DefaultMember / `[IndexerName]` name) -> its `get_`/`set_` accessor method, emitting the plain `clrInstance`
		// call — byte-identical to the old hardcoded `get_Item`/`set_Item` for the standard case, but correct for a
		// custom-named indexer. The receiver's type carries the element type arg (`Collection<Int>`), so the
		// constructed `clrg:...[int]` resolves the substituted accessor.
		val ixOwner = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
		if (recv != null && ixOwner != null && isExternalNetType(ixOwner)) {
			val mt = birType(recv.type); val a = regularArgs(call)
			// The get accessor returning a generic param (`IList<T>.get` -> T) reports the SUBSTITUTED ret (gp:T):
			// ilemit then hands back gp:T (matching the stack), so the value<->collection boundary box/unbox is
			// correctly typed (else a value-type instantiation NullRefs/garbages). Needs ClrRef("gp:") -> MapType.
			val retH = birType(call.type)
			// `virtual` for the fallback where bir2cir cannot resolve the owner and the raw `method:"get"/"set"` node
			// reaches ilemit (an open/override operator get/set must callvirt) — same rationale as the .NET-interop
			// callInstance path below (#139). bir2cir drops it when it reshapes the indexer to a clrInstance accessor.
			val ixVirtual = isVirtualInstanceCall(call, callee)
			return if (name == "get")
				"""{"k":"callInstance","virtual":$ixVirtual,"ownerType":${str(mt)},"method":"get","prop":"index-get","argTypes":[${birType(a[0].type).toJson()}],"ret":${str(retH)},"recv":${expr(recv)},"args":[${expr(a[0])}]${superTag(call)}}"""
			else
				"""{"k":"callInstance","virtual":$ixVirtual,"ownerType":${str(mt)},"method":"set","prop":"index-set","argTypes":[${birType(a[0].type).toJson()},${birType(a[1].type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"recv":${expr(recv)},"args":[${expr(a[0])},${expr(a[1])}]${superTag(call)}}"""
		}
	}

	// #60 (W1): a cross-module inline MEMBER (`body==null`, a DISPATCH receiver present) taking ANY lambda arg (AXIS ①)
	// MUST be source-inlined — a dll2klib-projected DotKt member AND a KLIB stdlib member alike. kotc is body-BLIND here
	// (the klib is metadata-only; the [KotlinInline] payload lives on the ref.dll), so it emits the owner-ful `callInline`
	// UNCONDITIONALLY and bir2cir — which holds the payload — makes the splice-or-fail-loud eligibility decision (it
	// resolves the payload off the ref.dll `InlineCandidates`, and its §4.3 rebinds the payload's `{k:this}` to the
	// caller-provided `recvs.dispatch`). This MUST run BEFORE the CLR-interop member block below: that block fires for ANY
	// projected .NET owner (`clrName(declaringClass) != null`) and would otherwise emit a plain `callInstance` + a REAL
	// delegate for the block, whose non-local `return` returns from the DELEGATE, not the caller — a SILENT miscompile.
	// The member-EXTENSION dual-receiver (#23) shape rides through too (both receivers carried): bir2cir splices the
	// SOUND pure-extension idiom (body reads only the extension `this`) and FAILS LOUD on a body that reads the dispatch
	// receiver (a `{k:this}`) — converting the old silent #23 gap to loud until W2 co-binds both receivers.
	if (inlineDecl.body == null && callNeedsSplice(call) && dispatchReceiver(call) != null)
		return emitOwnerfulInlineNode(call)

	// NEUTRAL .NET-interop fact-carrier selector (A2/#61 — REALIZED; NOT a .NET call-SHAPE decision). This block
	// decides NO CLR shape: it emits ONLY plain `callStatic`/`callInstance` nodes carrying frontend FACTS —
	// static-ness (callStatic vs callInstance, from receiver presence), the accessor KIND (`prop:"get"/"set"`,
	// from correspondingPropertySymbol), the indexed-access fact (`prop:"index-get"/"index-set"`), `typeArgs`+
	// declared `shapeTypes`, `argTypes`/`ret`, and the constructed-owner IDENTITY (the `memberType` supertype
	// walk). EVERY .NET shape — `clrStatic`/`clrInstance`/`clrPropGet`/`clrPropSet`/`clrGeneric*`, the indexer's
	// `get_Item`/`[IndexerName]` accessor slot, `op_X` operators — is decided BELOW the kotc boundary by
	// bir2cir's `NetInteropBinding`, which re-detects the .NET owner itself (ResolveNetType off the ref dlls),
	// independent of this gate. What differs from the plain-Kotlin member paths below is only the fact-carrier
	// DIALECT (`ownerType`+`argTypes`+`ret`+`prop` marker vs `owner`+`sig`+`retHint`) — the kotc↔bir2cir
	// serialization contract that routes a node to `NetInteropBinding` (ownerType-keyed) vs `MemberCallSubstitution`
	// (owner-keyed) — NOT a CLR decision. The `clrName` gate is a pure ORIGIN fact ("this owner is external",
	// read from the dll2klib-projected annotation — a frontend fact kotc is allowed to forward), NOT
	// an interpretation of `@Clr*` metadata or a BCL shape. The sole dialect EXCEPTION emitted here is `clrEventGet`
	// (a .NET event has no plain-Kotlin call form — CLR-only vocab, by design). An INHERITED .NET member (e.g.
	// `appError.Message`) is a fake-override whose `parent` is the Kotlin subclass, so resolve through the fake
	// override to the real .NET declaring type. A `kotlin.*` stdlib owner resolves to null here and FALLS THROUGH to
	// the plain Kotlin member-call path below (bir2cir substitutes it from the ref.dll).
	val clrTypeName = declaringClass?.let { clrName(it) }
		?: (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass)?.let { clrName(it) }
		// A restored external Kotlin companion may carry a CLR owner annotation; keep that exact owner identity.
		?: declaringClass?.takeIf { it.isCompanion }?.let { it.parent as? IrClass }?.let { clrName(it) }
	val clrType = clrTypeName?.let { TypeNode.Fqn(it) }
	if (clrType != null) {
		val recv = dispatchReceiver(call)
		// A restored external Kotlin companion is an instance singleton: keep its IrGetObjectValue receiver in BIR
		// (`Owner.INSTANCE`) and
		// let bir2cir decide whether the referenced owner is actually a CLR static class or an emitted Kotlin object.
		// Treating every object receiver as CLR static here erased the receiver of cross-module `Dispatchers.Default`.
		val isExternalCompanion = declaringClass?.isCompanion == true && clrName(declaringClass) != null
		val companionCallTag = if (isExternalCompanion) ""","companionCall":true""" else ""
		val isStatic = recv == null
		// A NON-static callInstance emitted here is normally reshaped to a `clrInstance` by bir2cir's
		// NetInteropBinding (which resolves the owner off the .NET refs) — where `virtual` is irrelevant. But a
		// DotKt library consumed AS KOTLIN whose owner bir2cir cannot resolve (netType == null -> left un-reshaped)
		// reaches ilemit as a raw `callInstance`; ilemit reads `virtual` to pick call vs callvirt. So stamp it here
		// exactly like the plain Kotlin member-call path: virtual unless FINAL and not an override. Without it ilemit
		// would default to a non-virtual `call`, mis-dispatching an `open`/`override` member (#139).
		val clrCallVirtual = isVirtualInstanceCall(call, callee)
		// Address the member on the CONSTRUCTED .NET type (`clrg:Collection[int]`) so a member of a generic
		// instantiation resolves. Two cases: (1) the receiver's own type IS the .NET type; (2) the member is
		// INHERITED from a .NET base (receiver is a Kotlin subclass) -> use the subclass's .NET supertype,
		// which carries the concrete type args (`class C : Collection<Int>`).
		val recvClass = recv?.type?.classifierOrNull?.owner as? IrClass
		// The REAL .NET declaring type (resolve the fake override; `declaringClass` would be the subclass).
		val declClass = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
		val memberType = when {
			isStatic -> clrType
			recvClass != null && isExternalNetType(recvClass) -> birType(recv.type)
			// A type-PARAM receiver (`destination: C` where `C : MutableCollection<T>`, e.g. filterTo's body) has no
			// recvClass -> use the type param's @Clr-bound BOUND with its args (clrg:ICollection[T]), not the raw
			// clrName (System.Collections.Generic.ICollection without `1 -> ResolveType fails).
			else -> (recvClass?.superTypes ?: (recv.type.classifierOrNull?.owner as? org.jetbrains.kotlin.ir.declarations.IrTypeParameter)?.superTypes)
				?.firstOrNull { it.classifierOrNull?.owner == declClass }?.let { birType(it) } ?: clrType
		}
		// A .NET event is NOT rewritten to an `add_<E>`/`remove_<E>` call here. It is surfaced as a
		// `kotlin.clr.ClrEvent<T>` property and consumed via `subscribe`; kotc emits the plain subscribe call (handled
		// at the top of this function), and bir2cir's ClrEventSubscriptionBinding binds it to add + close-token remove.
		// No `add_`/`remove_` naming, no clrEventAdd
		// in kotc — the Kotlin<->CLR event relation is bir2cir's (layer purity).
		// A generic .NET method (`Unsafe.SizeOf<T>()`, `Activator.CreateInstance<T>()`) -> resolve the open
		// generic-method definition from the frontend-resolved declaration, then carry its type arguments. The CLR has
		// reified generics, so this is just an ordinary generic-method call (no
		// erasure dance) — see [[clr-not-jvm-discard-jvmisms]]. Static -> clrGenericStatic, instance -> ...Instance.
		if (callee.typeParameters.isNotEmpty()) {
			val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
			if (targs.all { it != null }) {
				val taJson = targs.joinToString(",") { birType(it!!).toJson() }
				val member = name
				val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
				// A generic MEMBER extension (`class C { fun <R> T.f() }`): the `__self` receiver is the .NET method's
				// first param -> prepend its value + declaration type so the descriptor and call operands line up.
				val gExt = if (!isStatic) extensionReceiver(call) else null
				val shapeParams = (if (gExt != null) listOf(gExt.type) else emptyList()) + regularParams(callee).map { it.type }
				// kotc emits the DECLARED parameter types as PURE-KOTLIN `birType` identities (`shapeTypes`); bir2cir
				// hands them to bir2cir as `shapeTypes`; bir2cir alone resolves the physical scalar memberRef. No CLR-shape knowledge here.
				val shapeTypes = shapeParams.joinToString(",") { birType(it).toJson() }
				// Positional filling, like every other .NET/restored-member call path: building `args` from the
				// expressions that happen to be present DELETES an omitted default's slot, so a later provided
				// argument slides into it (`g.pick(b = 3)` bound `3` to `a` and left the required `b` zero-filled)
				// while `shapeTypes` above still describes the full parameter vector. `filledExternalArgs` emits a
				// metadata constant, a `defaultArg` placeholder for bir2cir's DefaultArgSplice, or a loud refusal.
				// FILL FIRST, then read the receiver: under an evaluation plan the fill binds the receiver, and this
				// read then renders that ONE binding (see [filledExternalArgs]).
				val gRegArgs = filledExternalArgs(call)
				val argsJson = (listOfNotNull(gExt?.let { expr(it) }) + gRegArgs).joinToString(",")
				// A `suspend` generic .NET-member callee carries the `"suspendCall":true` FACT for bir2cir's deferred
				// Task/await lowering, exactly like the non-generic call paths (suspendCallTag) — otherwise a generic
				// .NET-member suspend call would silently drop out of the suspension lowering. (latent ⑤.)
				// A2 (#61): a PLAIN call by identity carrying the generic FACTS (typeArgs + declared shapeTypes);
				// bir2cir's NetInteropBinding resolves the owner off the .NET refs and shapes it to clrGenericStatic/
				// clrGenericInstance (the `typeArgs` presence is the generic signal).
				return if (isStatic)
					"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[$argsJson]${suspendCallTag(callee)}$anySlotTag$companionCallTag}"""
				else
					"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"recv":${expr(recv!!)},"args":[$argsJson]${suspendCallTag(callee)}$anySlotTag${superTag(call)}$companionCallTag}"""
			}
		}
		val prop = callee.correspondingPropertySymbol?.owner
		if (prop != null) {
			// KLIB has no field declaration in Kotlin vocabulary, so dll2klib
			// exposes a CLR FieldDef as a property carrying @ClrField. Preserve
			// that explicit storage fact in BIR; bir2cir resolves the physical
			// field against the selected reference assembly.
			if (isClrField(prop)) {
				val fieldName = str(prop.name.asString())
				val ownerType = memberType!!.toJson()
				return if (isStatic) {
					if (callee === prop.setter)
						"""{"k":"staticFieldSet","ownerType":$ownerType,"name":$fieldName,"value":${expr(regularArgs(call).first())}$companionCallTag}"""
					else
						"""{"k":"staticField","ownerType":$ownerType,"name":$fieldName${retHint((memberType as? TypeNode.Fqn)?.args != null, call.type)}$companionCallTag}"""
				} else {
					val receiver = expr(recv!!)
					if (callee === prop.setter)
						"""{"k":"setFieldExpr","ownerType":$ownerType,"recv":$receiver,"name":$fieldName,"value":${expr(regularArgs(call).first())}$companionCallTag}"""
					else
						"""{"k":"field","ownerType":$ownerType,"recv":$receiver,"name":$fieldName${retHint((memberType as? TypeNode.Fqn)?.args != null, call.type)}$companionCallTag}"""
				}
			}
			// A `kotlin.clr.ClrEvent<T>` property read is legal ONLY as the receiver of `.subscribe(h)`, where
			// clrEventReceiverOk is set. A bare read (`val e = w.Changed`) would emit a
			// `clrPropGet get_<Event>` that no bir2cir rule strips -> a distant, diagnostic-free downstream failure.
			// A .NET event is not a first-class value, so reject it here at the source with a kotc compile error.
			if (!clrEventReceiverOk && callee === prop.getter
				&& callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
				hadError = true
				messageCollector?.report(CompilerMessageSeverity.ERROR,
					"a .NET event ('${prop.name.asString()}') is not a first-class value: it may only be used with " +
						"'.subscribe(handler)', not be read/assigned",
					locationOf(call))
				return """{"k":"unsupportedExpr","of":"clr-event-read-outside-subscription: ${prop.name.asString()}"}"""
			}
			// A2 step 3: the property's OWN Kotlin name IS the .NET slot identity (dll2klib projects the member under
			// its .NET name), so kotc reads NO CLR name here — it emits the bare property name + the accessor KIND
			// (`"prop":"get"/"set"`, a frontend fact from correspondingPropertySymbol). bir2cir's NetInteropBinding
			// resolves the exact CLR Property accessor from reference metadata.
			val pn = prop.name.asString()
			val recvJson = if (isStatic) "null" else expr(recv!!)
			// A restored MEMBER extension property (`class C { val T.p }`): no .NET property exists — it's a
			// accessor method on the dispatch type, with the extension receiver as `__self`.
			// A2 (#61): a PLAIN instance call by identity carrying the accessor KIND; bir2cir's NetInteropBinding
			// finds no .NET property `p` and applies the shared forward physical allocation.
			// The accessor's arg list is the one physical projection: `[__self?] + <positional args>`, where the
			// positional part carries a restored `context(...)` parameter and, for a setter, the trailing `value`.
			extensionReceiver(call)?.let { pExt ->
				val accArgIrs = listOf(pExt) + regularArgs(call)
				val accArgTypes = accArgIrs.joinToString(",") { birType(it.type).toJson() }
				val accArgs = accArgIrs.joinToString(",") { expr(it) }
				return if (callee === prop.setter)
					"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set"${overloadSigField(callee)},"argTypes":[$accArgTypes],"ret":${fqnJson("kotlin.Unit")},"recv":$recvJson,"args":[$accArgs]${superTag(call)}$companionCallTag}"""
				else """{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get"${overloadSigField(callee)},"argTypes":[$accArgTypes],"ret":${birType(callee.returnType).toJson()},"recv":$recvJson,"args":[$accArgs]${superTag(call)}$companionCallTag}"""
			}
			// A2 (#61): a `kotlin.clr.ClrEvent<T>` read is CLR-ONLY vocabulary — a .NET event has no plain-Kotlin
			// call form (it exposes add_/remove_, not a get_); dll2klib projects it purely to typecheck, so kotc
			// LOWERS it directly to a DEDICATED dialect node `clrEventGet` (the ClrEvent<T> handle) — NOT the
			// bir2cir-produced `clrPropGet` (which after A2 means a real .NET property). It exists ONLY to feed a
			// `subscribe`: bir2cir's ClrEventSubscriptionBinding consumes the `clrEventGet + call` pair
			// into an add_/remove_ accessor, so it never reaches ilemit (a bare event read is rejected above). Every
			// OTHER property is a plain Kotlin-shaped access -> emit an accessor call by identity;
			// NetInteropBinding shapes it to clrPropGet/clrPropSet (a .NET property OR field) off the refs.
			if (callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
				return """{"k":"clrEventGet","type":${memberType!!.toJson()},"name":${str(pn)},"static":$isStatic,"recv":$recvJson$companionCallTag}"""
			}
			val propCallKind = if (isStatic) "callStatic" else "callInstance"
			val propRecvField = if (isStatic) "" else ""","recv":$recvJson"""
			// A non-static property-accessor callInstance carries `virtual` too (moot once bir2cir reshapes it to
			// clrPropGet/clrPropSet; consistent with the other .NET-interop callInstance nodes — #139).
			val propVirtualField = if (isStatic) "" else ""","virtual":$clrCallVirtual"""
			val plainAccArgIrs = regularArgs(call)
			val plainAccArgTypes = plainAccArgIrs.joinToString(",") { birType(it.type).toJson() }
			val plainAccArgs = plainAccArgIrs.joinToString(",") { expr(it) }
			return if (callee === prop.setter)
				"""{"k":"$propCallKind"$propVirtualField,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set"${overloadSigField(callee)},"argTypes":[$plainAccArgTypes]$propRecvField,"args":[$plainAccArgs]${superTag(call)}$companionCallTag}"""
			else """{"k":"$propCallKind"$propVirtualField,"ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get"${overloadSigField(callee)},"argTypes":[$plainAccArgTypes],"ret":${birType(callee.returnType).toJson()}$propRecvField,"args":[$plainAccArgs]${superTag(call)}$companionCallTag}"""
		}
		val member = name
		val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
		val argsJson = regularArgs(call).joinToString(",") { expr(it) }
		// kotc emits the PLAIN Kotlin return type; a `suspend` callee is marked by `suspendTag` only (the Task/await
		// lowering is a deferred downstream layer). No coroutine ABI (Task<T>) is baked here.
		val ret = birType(callee.returnType).toJson()
		val suspendTag = suspendCallTag(callee)
		// A .NET operator (`Vec2 + Vec2` -> op_Addition) is emitted here as the PLAIN Kotlin operator identity
		// (`callInstance method="plus" recv:<a> args:[<b>]`); bir2cir's NetInteropBinding resolves the owner off
		// the .NET refs, confirms the CLR type declares the `op_X` static, and reshapes it to a `clrStatic op_X`
		// with the receiver prepended. No `op_` naming / receiver-prepend here (layer purity — CLR knowledge is bir2cir's).
		// A .NET extension method `static M(this T self, …)` exposed as a Kotlin extension `fun T.m()` on a @Clr
		// object: it's a STATIC call whose first argument is the extension receiver.
		val extRecv = extensionReceiver(call)
		if (isStatic && extRecv != null) {
			val (allArgs, allArgTypes) = clrCallArgsWithRecv(call, callee, extRecv)
			return """{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)}${overloadSigField(callee)},"argTypes":[$allArgTypes],"ret":$ret,"args":[$allArgs]$suspendTag$anySlotTag$companionCallTag}"""
		}
		// A restored MEMBER extension function (`class C { fun T.f() }`): an INSTANCE method on the dispatch receiver
		// (C) whose first .NET param `__self` is the extension receiver -> dispatch on `recv`, prepend the receiver.
		if (!isStatic && extRecv != null && recv != null) {
			val (allArgs, allArgTypes) = clrCallArgsWithRecv(call, callee, extRecv)
			return """{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)}${overloadSigField(callee)},"argTypes":[$allArgTypes],"ret":$ret,"recv":${expr(recv)},"args":[$allArgs]$suspendTag$anySlotTag${superTag(call)}$companionCallTag}"""
		}
		// A2 (#61): a PLAIN static/instance call by the .NET owner's FQN identity; bir2cir's NetInteropBinding
		// resolves the owner off the .NET refs and shapes it (clrStatic/clrInstance). No .NET-shape decision here.
		val (cArgs, cArgTypes) = clrCallArgs(call, callee)
		return if (isStatic)
			"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)}${overloadSigField(callee)},"argTypes":[$cArgTypes],"ret":$ret,"args":[$cArgs]$suspendTag$anySlotTag$companionCallTag}"""
		else
			"""{"k":"callInstance","virtual":$clrCallVirtual,"ownerType":${memberType!!.toJson()},"method":${str(member)}${overloadSigField(callee)},"argTypes":[$cArgTypes],"ret":$ret,"recv":${expr(recv!!)},"args":[$cArgs]$suspendTag$anySlotTag${superTag(call)}$companionCallTag}"""
	}

		// An EXTERNAL top-level property (from a DotKt assembly) -> the referenced .NET file class holds it. An
		// EXTENSION property (`val T.p`) surfaces as an accessor static with the extension receiver
		// passed as `__self`; a plain field-backed NON-extension property (`val greeting`) is a STATIC FIELD, so
		// read -> `staticField` / write -> `staticFieldSet` of that referenced file class (#34b). BUT a field-backed
		// property with a CUSTOM accessor (`val x = 41; get() = field + 1`, #103) additionally emits an accessor
		// method on the file class — reading/writing the raw field would SKIP it (a silent cross-module miscompile).
		// (body==null = external declaration.)
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			val externalPropertyOwner = clrExternalOwner(p)
			if (declaringClass == null) externalPropertyOwner?.let { fileClass ->
				val companionTag = companionReceiverCallTag(p, call)
				// A projected companion extension has no runtime receiver. Keep its Kotlin source identity in BIR;
				// bir2cir binds that identity to the trusted producer carrier's exact CLR member.
				if (isCompanionExtensionCallee(p) || companionTag.isNotEmpty()) {
					val setter = callee === p.setter
					// Context parameters remain ordinary physical accessor arguments even though the companion receiver
					// itself has no runtime value. A lazy cross-module setter can still retain its source `value` in the
					// phantom extension-receiver slot; append that last argument only when the regular positional projection
					// did not already include it.
					val positional = regularArgs(call)
					val values = if (setter) {
						val value = call.arguments.lastOrNull { it != null }
						if (value != null && positional.none { it === value }) positional + value else positional
					} else positional
					val tag = companionTag
					if (isClrField(p)) {
						return if (setter)
							"""{"k":"staticFieldSet","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())},"value":${expr(values.first())}$tag}"""
						else if (isLateinitProperty(p))
							"""{"k":"lateinitGet","ownerType":${fqnJson(fileClass)},"static":true,"name":${str(p.name.asString())}$tag}"""
						else """{"k":"staticField","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())}$tag}"""
					}
					val argTypes = values.joinToString(",") { birType(it.type).toJson() }
					val args = values.joinToString(",") { expr(it) }
					val ret = if (setter) fqnJson("kotlin.Unit") else birType(callee.returnType).toJson()
					return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":${str(if (setter) "set" else "get")}${overloadSigField(callee)},"argTypes":[$argTypes],"ret":$ret,"args":[$args]$tag}"""
				}
				// An accessor that takes ANY argument — an extension receiver, or a `context(...)` parameter — is a
				// method on the file class, never a static field: route it to the accessor path
				// below. (A plain field-backed property's accessor takes none.)
				val isExt = p.getter?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver || isValueParameter(it) } == true
				if (!isExt) {
					// #103: a field-backed prop with a CUSTOM getter/setter must INVOKE the accessor (a static
					// accessor method on the file class, like the extension-property path below but without a
					// receiver), not read/write the raw static field. bir2cir binds the explicit get/set role to the
					// associated method. Read/write customness is independent (a `var` may pair a
					// custom setter with a default getter, or vice versa); a default accessor stays a raw field access.
					// dll2klib preserves the standard getter_flags/setter_flags IS_NOT_DEFAULT bit.
					val customGet = !hasDefaultGetter(p)
					val customSet = !hasDefaultSetter(p)
					if (callee === p.setter) {
						return if (customSet)
							"""{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"set"${overloadSigField(callee)},"argTypes":[${birType(regularArgs(call).first().type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"args":[${expr(regularArgs(call).first())}]}"""
						else """{"k":"staticFieldSet","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
					}
					return if (customGet)
						"""{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"get"${overloadSigField(callee)},"argTypes":[],"ret":${birType(callee.returnType).toJson()},"args":[]}"""
					else """{"k":"staticField","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())}}"""
				}
				val recv = extensionReceiver(call)
				// A2 (#61 / step 3): a top-level extension property accessor is a static method receiving `__self`
				// on the referenced file class (NOT a .NET property) -> emit the plain static call by identity carrying
				// the accessor role; bir2cir finds no matching .NET property/field and applies the shared forward allocation.
				if (callee === p.setter) {
					val args = listOfNotNull(recv) + regularArgs(call)
					return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"set"${overloadSigField(callee)},"argTypes":[${args.joinToString(",") { birType(it.type).toJson() }}],"ret":${fqnJson("kotlin.Unit")},"args":[${args.joinToString(",") { expr(it) }}]}"""
				}
				// The getter's args are the SAME projection every other call uses: `[__self?] + <positional args>` —
				// the positional part is empty for a plain extension property and carries the `context(...)` arguments
				// for a context property.
				val getArgs = listOfNotNull(recv) + regularArgs(call)
				return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(p.name.asString())},"prop":"get"${overloadSigField(callee)},"argTypes":[${getArgs.joinToString(",") { birType(it.type).toJson() }}],"ret":${birType(callee.returnType).toJson()},"args":[${getArgs.joinToString(",") { expr(it) }}]}"""
			}
		}

	// Top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING file's
	// class. Use the property's own file, NOT the file currently being emitted — else a cross-file reference
	// looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
	(callee.correspondingPropertySymbol?.owner)?.let { p ->
		if (declaringClass == null) {
			val companionPropertyCallTag = companionReceiverCallTag(p, call)
			val ext = extensionReceiver(call)
			// C7: a TOP-LEVEL EXTENSION property (`val List<T>.lastIndex`, `val Int.absoluteValue`, `val
			// CharSequence.indices`) has NO real static field — its value is an accessor emitted by the property's
			// OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78/#81) whose leading arg is the extension
			// receiver. Emit it `owner=null`, so bir2cir attributes it to the ref.dll file class in a cross-module app
			// build (the owner-null top-level substitution axis — UNTOUCHED). It ALSO carries `calleeOwner` (#199 Design
			// B, same two-axis contract as a top-level FUNCTION call): a same-module same-simple-name extension property
			// across two packages disambiguates by the FIR-resolved file-class DISPATCH hint at ilemit, without shadowing
			// substitution. bir2cir binds referenced properties from metadata and allocates local physical names from the
			// same explicit identity. `sig` disambiguates a same-name overload
			// by receiver type. A cross-module DESERIALIZED stub can spuriously report a backing field, so an
			// extension property must NEVER fall to the static-field read below — that dropped the receiver and looked
			// up `<CurrentFileKt>.<name>` as a field (the C7 `field AppKt.lastIndex not found` crash).
			if (ext != null) {
				// A GENERIC extension property (`val List<T>.lastIndex`/`.indices`) has a generic <name>[T] accessor —
				// carry the resolved type args (+ a retType hint) so ilemit MakeGenericMethods it; without them the call
				// hits the uninstantiated generic method ("type is not fully instantiated"). Mirrors the generic
				// extension-FUNCTION path. A non-generic getter (Int.absoluteValue, CharSequence.lastIndex) emits no ta.
				val ta = typeArgsJson(call)
				return if (callee === p.setter) {
					val args = listOf(ext) + regularArgs(call)
					"""{"k":"callStatic","owner":null,"method":${str(p.name.asString())},"prop":"set"${overloadSigField(callee)}$ta,"args":[${args.joinToString(",") { expr(it) }}]${calleeOwnerTag(p)}$companionPropertyCallTag}"""
				} else
					"""{"k":"callStatic","owner":null,"method":${str(p.name.asString())},"prop":"get"${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), birType(call.type))},"args":[${(listOf(ext) + regularArgs(call)).joinToString(",") { expr(it) }}]${calleeOwnerTag(p)}$companionPropertyCallTag}"""
			}
			// A plain top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING
			// file's class. Use the property's own file, NOT the file currently being emitted — else a cross-file
			// reference looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
			// #89: fileClassOf returns the DECLARING file class only when the property is SAME-MODULE (its parent is a
			// real IrFile). A CROSS-MODULE property is a lazy declaration deserialized from a dependency (the frontend
			// metadata klib, which is PACKAGE-keyed — the file grouping survives ONLY in the ref.dll bir2cir reads), so
			// its parent is a package fragment, NOT an IrFile, and fileClassOf falls back to the READING file's class —
			// mis-attributing e.g. a cross-module `COROUTINE_SUSPENDED` read to `<ReaderFile>Kt` (the #80 root that
			// forced a bir2cir owner-rebind band-aid). kotc genuinely CANNOT name the declaring file class here (it is
			// CLR/ref knowledge), so for the ACCESSOR (`prop:get`/`prop:set`) emission it declares the owner UNRESOLVED
			// (`owner:null`) — the SAME honest fact it emits for a cross-module top-level FUNCTION — and bir2cir binds
			// the true declaring file class off the ref.dll (its owner-null top-level resolver), no wrong-owner rebind.
			// (A raw cross-module static FIELD read cannot be owner-null-resolved and has no reachable case — every such
			// top-level val is a computed accessor — so the staticField branches keep the fileClassOf owner.)
			val crossModule = p.parent !is IrFile
			val owner = fileClassOf(p)
			val accessorOwner = if (crossModule) "null" else fqnJson(owner)
			// A COMPUTED top-level property (`val foo: T get() = ...`, no backing field) OR one that has a backing
			// field (initializer) but ALSO a CUSTOM accessor (`val foo = 41; get() = field + 1`, #89) -> a static
			// call by the property's OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78/#81), NOT the
			// physical accessor name and not a raw static-field load (which would skip the custom accessor).
			// bir2cir binds referenced properties from metadata or applies the shared local forward allocation. The
			// read/write decisions are independent: a
			// `var` may pair a default getter (field read) with a custom setter (accessor call), or vice versa.
			return if (callee === p.setter) {
				if (!writesAsStaticField(p))
					"""{"k":"callStatic","owner":$accessorOwner,"method":${str(p.name.asString())},"prop":"set"${overloadSigField(callee)},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]$companionPropertyCallTag}"""
				else
					"""{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}$companionPropertyCallTag}"""
			} else {
				if (!readsAsStaticField(p))
					"""{"k":"callStatic","owner":$accessorOwner,"method":${str(p.name.asString())},"prop":"get"${overloadSigField(callee)},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]${retHint(false, call.type)}$companionPropertyCallTag}"""
				else if (isLateinitProperty(p))
					"""{"k":"lateinitGet","ownerType":${fqnJson(owner)},"static":true,"name":${str(p.name.asString())}$companionPropertyCallTag}"""
				else
					"""{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())}$companionPropertyCallTag}"""
			}
		}
	}

	// `s.length` on a String is NOT intercepted here: it's a real `kotlin.String.length` property read — fall
	// through to the ordinary property-get path as `method:length, prop:get`. The
	// CLR binding (String.length -> System.String.Length) is stdlib `@ClrIntrinsic("Length")` metadata, applied
	// by bir2cir's MemberCallSubstitution (the sibling `String.get`->`get_Chars` was cleaned the same way). kotc
	// carries NO CLR knowledge here (layer boundary — CLAUDE.md §"kotc reads NEITHER @ClrIntrinsic…").
	// Pair/Triple `.first`/`.second`/`.third` and IndexedValue `.index`/`.value` are NOT intercepted: they are real
	// `kotlin.Pair`/`kotlin.Triple`/`kotlin.collections.IndexedValue` property reads — fall through to the ordinary
	// member-property-read path so they emit semantic getter calls. Their stdlib backing
	// fields are accessor-routed (internal), so a raw cross-assembly field read never binds directly; the faithful
	// property call is what ilemit already resolves (its external-owner field node re-routes to the getter anyway).

	// Property get/set on a user class -> field access.
	val property = callee.correspondingPropertySymbol?.owner
	// `.size` -> CIL array length (arrays) or `Enumerable.Count` (collections).
	if (property?.name?.asString() == "size") dispatchReceiver(call)?.let { r ->
		if (isArrayType(r.type)) return """{"k":"arrayLen","array":${expr(r)}}"""
		// `Color.entries.size`: entries -> a Color[] (enumValues), so .size is the array length.
		if (r.type.classFqName?.asString() == "kotlin.enums.EnumEntries") return """{"k":"arrayLen","array":${expr(r)}}"""
		// kotlin.* collection/map `.size` is NOT intercepted: it's a real `size` property — fall through to the
		// ordinary property read so it emits as a semantic Kotlin property call.
	}
	// `kProperty.name` is NOT intercepted here (#70): `kotlin.reflect.KProperty*`/`KCallable.name` is a REAL
	// emitted stdlib interface member now (kotc's `propertyRef`/`kPropertyStub` materialize real implementations
	// of it) — it falls through to the ordinary member-property-read path below, emitting the SAME
	// `callInstance ownerType:kotlin.reflect.KProperty(/KCallable) method:name prop:get` shape this used to hand-roll,
	// just with the real FQN instead of the retired `dotkt$KProperty` synthetic.
	if (property != null && declaringClass != null) {
		val recvExpr = dispatchReceiver(call)
		val recv = recvExpr?.let { expr(it) } ?: """{"k":"this"}"""
		val ownerStr = ownerSpec(declaringClass, recvExpr?.type)
		val owner = str(ownerStr)
		// A property with a custom accessor — OR one overriding an interface property (e.g. CharSequence.length) —
		// routes through an accessor, not the backing field. Physical binding belongs to bir2cir and uses the explicit
		// property identity plus the `overrides` marker.
		if (!isLateinitProperty(property) && !isClrField(property)) {   // route through accessor; @ClrField reads/writes the plain field
			val virtual = isVirtualInstanceCall(call, callee)
			// A member extension property dispatches on the enclosing C; its accessor takes the extension receiver as a
			// leading `__self` argument.
			val pExt = extensionReceiver(call)?.let { expr(it) }
			// The accessor's arg list is the SAME projection a function call uses: the leading `__self` (member
			// extension), then the [isValueParameter] sequence — a `context(c: Ctx) val p` accessor's context
			// parameters, and for a setter the `value` parameter that follows them.
			val accArgs = (listOfNotNull(pExt) + regularArgs(call).map { expr(it) }).joinToString(",")
			return if (callee === property.setter)
				"""{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(property.name.asString())},"prop":"set"${overloadSigField(callee)},"args":[$accArgs]${overridesJson(callee)}${superTag(call)}}"""
			else """{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(property.name.asString())},"prop":"get"${overloadSigField(callee)},"args":[$accArgs]${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}${overridesJson(callee)}${superTag(call)}}"""
		}
		return if (callee === property.setter)
			"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
		// `lateinit var` read -> throw if still uninitialized (the field is null) — proper lateinit semantics.
		else if (isLateinitProperty(property))
			"""{"k":"lateinitGet","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}}"""
		else """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}}"""
	}

	// Kotlin universal methods (hashCode/toString/equals) on a builtin receiver. The System.Object slot is correct
	// ONLY for a GENUINE universal call — one whose receiver TYPE does not declare its OWN routable override:
	//  - the resolved callee is the inherited kotlin.Any member (a fake override): Int/Long/Char/Boolean.hashCode,
	//    or a bare List/Set/Map.toString (emitted as objMethod ToString with a `recvType` hint; bir2cir routes it
	//    Kotlin-style), or Any/generic; and
	//  - a PRIMITIVE value type's toString/equals — those are declared but bodyless (no Kotlin body to hoist, no
	//    @ClrIntrinsic), so bir2cir has nothing to route to and the BCL value type's ToString/Equals IS correct;
	//    and Int/Long/Char/Boolean/Float/Double's hashCode, which the CLR stdlib does NOT declare (it inherits the
	//    kotlin.Any slot), so it stays objMethod → the BCL value type's GetHashCode (#167/#168).
	// When the receiver TYPE declares its OWN routable override — String's @ClrIntrinsic hashCode/toString/equals,
	// a Pair|Triple|data-class toString (→ C11) — the call must REACH that member, so FALL THROUGH to the ordinary
	// member-call path (bir2cir routes it: a real body → rule-3 helper, an @ClrIntrinsic → its BCL slot). Routing a
	// declared override to System.Object here shadows the correct Kotlin body — the C11 miscompiles.
	if (isBuiltin && dispatchReceiver(call) != null) {
		// The receiver TYPE declares its OWN override iff the resolved callee is a real (non-fake-override) member of a
		// type OTHER than kotlin.Any. A call resolved DIRECTLY to `kotlin.Any.hashCode/toString/equals` — e.g.
		// `element.toString()` on a generic `T` with no more-derived override — is NOT a fake override yet IS the
		// universal method, so it must keep the System.Object slot (falling through would emit a call to the
		// non-existent `kotlin.Any.toString` and NRE). Hence the explicit kotlin.Any exclusion beside isFakeOverride.
		val declaresOwn = !callee.isFakeOverride && declaringClass?.fqNameWhenAvailable?.asString() != "kotlin.Any"
		val primitive = dispatchReceiver(call)!!.type.isPrimitiveOrUnsigned()
		// A `super.toString()`/`super.hashCode()`/`super.equals()` (issue #14) resolving to the kotlin.Any slot must NOT
		// become an `objMethod` — that is UNCONDITIONALLY a `callvirt object::…` in ilemit, which re-dispatches by the
		// receiver's runtime type back to THIS class's override and infinite-loops. Fall through to the ordinary
		// member-call path, which emits a NON-virtual `callInstance` (isVirtualInstanceCall → virtual:false) carrying
		// `anySlot:true`; bir2cir renames the slot + resolves the kotlin.Any owner to System.Object, ilemit's `call`
		// reaches the base slot exactly like C#'s `base.ToString()`. The receiver of a super call is always `this` (a
		// reference class), never a primitive, so this never disturbs the value-type objMethod routing.
		val isSuper = call.superQualifierSymbol != null
		val fallThrough = isSuper || when (name) {
			"hashCode" -> declaresOwn                      // Int/Long/Char/Boolean/Float/Double inherit Any.hashCode → stays objMethod (String's @ClrIntrinsic hashCode falls through)
			"toString", "equals" -> declaresOwn && !primitive
			else -> false
		}
		if (!fallThrough) when (name) {
			"hashCode" -> return """{"k":"objMethod","method":"hashCode","recv":${expr(dispatchReceiver(call)!!)}}"""
			"toString" -> if (regularArgs(call).isEmpty()) {
				// Emit the FAITHFUL objMethod toString. bir2cir recovers the receiver's static type via StaticType (no
				// kotc hint) and, for a collection/Map receiver, routes to the Kotlin-style clrCollToString /
				// clrMapToString helper (`[a, b]` / `{a=1, b=2}`); else it renames to the .NET ToString slot.
				val recvE = dispatchReceiver(call)!!
				return """{"k":"objMethod","method":"toString","recv":${expr(recvE)}}"""
			}
			"equals" -> {
				val recvE = dispatchReceiver(call)!!; val argE = regularArgs(call).first()
				// Emit the FAITHFUL objMethod equals. An EXPLICIT `.equals()` on a boxed Double/Float / a collection
				// follows Kotlin's TOTAL order / STRUCTURAL equality (Object.Equals gives IEEE
				// `(-0.0).equals(0.0)==true` / reference identity), so bir2cir recovers the receiver/arg static types
				// via StaticType (no kotc hint) and routes to the SAME helper the EQEQ path uses; else it keeps
				// Object.Equals.
				return """{"k":"objMethod","method":"equals","recv":${expr(recvE)},"arg":${expr(argE)}}"""
			}
		}
	}
	// `n.toString(radix)` is NOT lowered in kotc (C4, 2026-07-06). The former `System.Convert.ToString(value, base)`
	// special-case was BOTH a layer violation (a BCL name in kotc) AND wrong: Convert.ToString renders a negative in
	// two's-complement (`(-255).toString(16)` -> "ffffff01", not "-ff") and THROWS for a base outside {2,8,10,16}
	// (`35.toString(36)` -> ArgumentException "Invalid Base"). The stdlib actual (StringNumberConversionsClr.kt) has
	// the correct sign-and-arbitrary-digit body; kotc now emits the plain `kotlin.text` Int/Long.toString(radix)
	// extension call and bir2cir attributes it to StringNumberConversionsKt so the real body runs.

	if (isBuiltin) {
		val operands = call.arguments.filterNotNull()
		// `String + x` (concatenation, not numeric add) is NOT recognized here: kotc emits the plain
		// `callInstance kotlin.String.plus` (a faithful member call) via the general member-call path, and bir2cir's
		// PrimitiveOperatorLowering re-emits the `concat` (recovering each part's static type via StaticType, applying
		// the collection/nullable part routing) — the `String.plus -> concat` MEMBER recognition is bir2cir's.
		// `==` (EQEQ) / `===` (EQEQEQ) are `kotlin.internal.ir` COMPILER INTRINSICS. ALL of the ceq-vs-Object.Equals
		// SPLIT + the Kotlin-SEMANTIC structural routings (collection `==`, boxed Double/Float total-order `==`)
		// recognition lives in bir2cir: kotc emits ONLY the FAITHFUL intrinsic call with owner =
		// `kotlin.internal.ir` (collision-safe). PrimitiveOperatorLowering recovers the operands' SURFACE static type
		// (prim fast-path -> ceq) and VALUE static type (collection/float helpers, else objEq) via StaticType; no
		// argTypes/argValueTypes hints are emitted — the operand expression nodes + the local env carry the types.
		if (name == "EQEQ" && operands.size == 2)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
		if (name == "EQEQEQ" && operands.size == 2)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
		// The IR comparison intrinsics (`kotlin.internal.ir.less`/`lessOrEqual`/`greater`/`greaterOrEqual` — the
		// `<`/`<=`/`>`/`>=` desugarings, top-level with plain value params). Recognition + operand shaping is
		// bir2cir's: kotc emits ONLY the FAITHFUL intrinsic call with owner = its home package `kotlin.internal.ir`
		// (collision-safe — a user top-level `less` is NOT `isBuiltin` and never has this owner), args = the plain
		// operand expressions. bir2cir's PrimitiveOperatorLowering re-emits `{k:binOp, op:<}` and does the operand
		// shaping (primitive gating, nullable-primitive `Nullable<T>.Value` unwrap, boxed-Any -> concrete cast) via
		// StaticType — exactly like EQEQ/EQEQEQ above. The Kotlin<->CLR relation lives there, not in kotc.
		if (name in setOf("less", "lessOrEqual", "greater", "greaterOrEqual") && operands.size == 2
				&& callee.parameters.none { it.kind == IrParameterKind.ExtensionReceiver })
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":${str(name)},"args":[${expr(operands[0])},${expr(operands[1])}]}"""
		// UNARY (unaryMinus/unaryPlus/not/inv) recognition is bir2cir's: kotc emits the faithful
		// `callInstance kotlin.Int.unaryMinus()` (0-arg member) and bir2cir re-emits `{k:unaryOp}` from the
		// PRIMITIVE_OP_FQ owner. The receiver is value-shaped by the general callInstance path (recvExpr).
		// `i.inc()`/`i.dec()` (the `i++`/`i--` desugaring) recognition is bir2cir's: kotc emits
		// the faithful `callInstance kotlin.Int.inc()` (0-arg member, receiver value-shaped by recvExpr) and
		// PrimitiveOperatorLowering re-emits `(recv + 1)`/`(recv - 1)` (the `const 1:kotlin.Int` literal moves there).
		// Numeric conversion `x.toLong()`/`x.toInt()`/… is not recognized here: kotc emits the plain
		// `callInstance kotlin.Int.toLong` (the faithful IR); bir2cir reads the `@kotlin.clr.ClrConv` marker off the
		// stdlib primitive's conversion member on the ref.dll and emits the `conv` node from the callee's return type.
		// `println(...)`/`print(...)` are NOT recognized here: kotc emits the plain top-level `callStatic owner:null`
		// via the general call path, and bir2cir substitutes it to System.Console.Write/WriteLine off the stdlib
		// @ClrIntrinsic (runtime/stdlib/clr/kotlin/io/ConsoleClr.kt) and wraps a collection/Map arg in
		// clrCollToString/clrMapToString (Kotlin-style `[a, b]`) — recovering the operand static types via StaticType.
		// `readLine()` is NOT lowered: the CLR stdlib exposes readln()/readlnOrNull() (readlnOrNull is @ClrIntrinsic-bound
		// to System.Console.ReadLine in ConsoleClr.kt). There is no `kotlin.io.readLine` symbol in the frontend KLIB.
		// Regex is NOT lowered here: `kotlin.text.Regex` is
		// @ClrTypeAlias("System.Text.RegularExpressions.Regex") with `containsMatchIn`@ClrIntrinsic("IsMatch") /
		// `replace`@ClrIntrinsic("Replace") + real Kotlin bodies for `matches`/`find`/`split`/`.value`
		// (runtime/stdlib/clr/kotlin/text/regex/RegexClr.kt). kotc emits `"p".toRegex()` as a plain call to the stdlib
		// `String.toRegex()` extension (= `Regex(this)`) and `r.containsMatchIn(s)`/`r.replace(...)` as plain member
		// calls on kotlin.text.Regex; bir2cir substitutes the @ClrTypeAlias ctor + @ClrIntrinsic members off the
		// ref.dll and runs the real bodies. The Kotlin<->CLR relation lives in bir2cir, not kotc.
		// `String.format` is NOT lowered here. System.String.Format would be CLR knowledge in kotc, and it is
		// dead against the frontend KLIB anyway — that jar has no `kotlin.text.String.Companion.format`, so the
		// symbol is unresolved before the backend ever runs. Making `String.format` work is a stdlib concern (bind a
		// `String.Companion.format(String, vararg Any?)` @ClrIntrinsic("System.String.Format")), NOT a kotc lowering.
		// `noWhenBranchMatchedException` / `throwUninitializedPropertyAccessException` are COMPILER INTRINSICS (the
		// exhaustive-when synthetic-else / uninitialized-property-access throws), siblings of ieee754equals/EQEQ/... —
		// kotc emits ONLY the FAITHFUL intrinsic call with owner = the callee's real resolved parent FQN
		// (collision-safe). bir2cir re-emits the throw (Kotlin IllegalStateException, substituted to the BCL type via
		// the ref.dll @ClrTypeAlias). The recognition + throw synthesis is bir2cir's, not kotc's.
		// NOTE: on THIS (CLR) pipeline only `noWhenBranchMatchedException` actually reaches here (top-level, owner
		// `kotlin.internal.ir`); a `lateinit` access lowers to a dedicated `lateinitGet` node earlier, so
		// `throwUninitializedPropertyAccessException` is never produced — its name-branch is defensive.
		if (name == "noWhenBranchMatchedException" || name == "throwUninitializedPropertyAccessException") {
			// FAITHFUL owner = the callee's real resolved parent FQN (the home package for the top-level intrinsic;
			// the enclosing class if a member-form callee ever appears). The final literal is an unreachable
			// last-resort default, not a preferred guess — the resolved FQN always wins ahead of it.
			val intrinsicOwner = declaringClass?.fqNameWhenAvailable?.asString()
				?: pkgFqName
				?: callee.fqNameWhenAvailable?.asString()?.substringBeforeLast('.', "")?.takeIf { it.isNotEmpty() }
				?: "kotlin.internal.ir"
			return """{"k":"callStatic","owner":${fqnJson(intrinsicOwner)},"method":${str(name)},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
		}
		// `ieee754equals` is a `kotlin.internal.ir` COMPILER INTRINSIC, a sibling of EQEQ/EQEQEQ/less/... — kotc
		// emits ONLY the FAITHFUL intrinsic call with owner = `kotlin.internal.ir` (collision-safe); bir2cir's
		// PrimitiveOperatorLowering re-emits the `binOp ==` (the ordered IEEE-754 comparison). The Kotlin<->CLR
		// relation lives there, not in kotc.
		if (name == "ieee754equals" && regularArgs(call).size == 2) {
			val a = regularArgs(call)
			return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"ieee754equals","args":[${expr(a[0])},${expr(a[1])}]}"""
		}
		// The top-level precondition / error helpers (`kotlin.TODO`/`error`/`require`/`check`/`requireNotNull`/
		// `checkNotNull`) are NOT special-cased here. The no-lambda overloads fall through to the general top-level
		// call path (`callStatic owner:null method:<name> args:[...]`); bir2cir recognizes them by callee name and
		// synthesizes the throw / condition. The lambda-taking overloads (`require(c){msg}` etc.) route through the
		// owner-less `callInline` node below (AXIS ①: any lambda arg splices), and bir2cir splices the real body.
		// Either way the Kotlin-semantic lowering lives in bir2cir, not kotc.
		// `coerceAtMost`/`coerceAtLeast`/`coerceIn` are NOT lowered here (layer purity).
		// System.Math.Min/Max/Clamp would be a BCL name in kotc (a layer violation). The stdlib
		// `_Ranges.kt` funcs are pure Kotlin with correct bodies (`if (this < min) min else this`), so kotc now emits a
		// plain call and the real stdlib body runs. This is also MORE correct than Math.Min for floats: Kotlin's coerce
		// uses `<`/`>` (total-ordering / NaN-propagating) semantics that differ from System.Math.Min/Max on NaN.
		// (No @ClrIntrinsic needed: the pure body IS the binding — the top-preferred "emit the real body" outcome.)
		// `repeat(n) { i -> body }` is NOT special-cased here (#75): a LITERAL lambda (AXIS ①) rides the general
		// owner-less `callInline` gate (bir2cir splices `kotlin.repeat` off the ref.dll and wraps the counted loop);
		// a callable-ref / non-lambda action falls through to the plain top-level call, which bir2cir's
		// RepeatInlineLowering re-emits as a delegate counter loop.
		// `kotlin.math.*` is NOT lowered here. kotc emits a plain call to the stdlib fun (owner=null callStatic /
		// an extension instance for Double.pow); bir2cir's MemberCallSubstitution reads MathClr.kt's @ClrIntrinsic
		// bindings off the ref.dll and substitutes System.Math.* / System.MathF.* — the CLR relation lives there, not
		// in kotc.
		// `kotlin.text` String ops are NOT name-lowered in kotc: kotc emits a plain call; bir2cir attributes it to
		// StringsKt and the StringCharSequenceBridge (run on the RT stdlib build too) coerces the String receiver/args
		// into the `dotkt$CharSequence` adapter so the CharSequence-extension body runs (contains/indexOf/startsWith/
		// endsWith/split/substring/isEmpty/isNotEmpty/uppercase/lowercase/isBlank/reversed/etc.). `reversed` is a plain
		// call too: the real stdlib `CharSequence.reversed() = StringBuilder(this).reverse()` runs — bir2cir's TransformNew
		// coerces the CharSequence ctor arg to String so `StringBuilder(String)` binds. No CLR lowering in kotc.
	}

	// DotKt round-trip: a call to a top-level function restored from a [KotlinFile] facade in a referenced
	// assembly -> a .NET static call on that file-facade class. `body == null` distinguishes the external symbol
	// from a same-named local top-level fun. (A suspend top-level fun awaits via the coroutine path, not here.)
	if (callee.body == null && dispatchReceiver(call) == null) {
		val extRecv = extensionReceiver(call)
		val externalFileClass = clrExternalOwner(callee)
		externalFileClass?.let { fileClass ->
			// A dll2klib-projected cross-module `inline fun` taking ANY lambda arg (AXIS ①) MUST be source-inlined: emit a
			// generic `callInline` node carrying the call bindings; bir2cir OWNS the splice (it re-lowers the carried body
			// in the app context, so a non-local `return`/`break`/suspend through a spliced lambda works, and a noinline
			// arg rides as a delegate — AXIS ②). A lambda-less inline call is NOT gated here — it falls through to the
			// plain call below, where the callee is a real generic method (the JIT inlines it). This fires ONLY for a
			// externally named fileClass; the receiver-carrying stdlib scope/util fns have no fileClass and take the
			// owner-less path below. An EXTENSION receiver (`Cell<T>.update { … }`, #133 case1) rides through
			// `inlineSpliceCall` in `recvs.extension` — the SAME shape the owner-less path threads, spliced onto payload
			// param[0] (`__self`) by bir2cir.
			if (callNeedsSplice(call)) return inlineSpliceCall(call, fileClass)
			// PLAIN static call by identity to the referenced .NET file class (bir2cir's NetInteropBinding shapes it
			// to clrStatic / clrGenericStatic). This is the fall-through for a lambda-less inline call (the callee is a
			// real generic method) as well as every non-inline top-level fun.
			return plainExternalTopLevelCall(call, callee, fileClass, name, extRecv)
		}
		// Any OTHER cross-module inline+lambda fun with no external fileClass — the whole stdlib rides the KLIB, so
		// scope/util fns (let/run/with/apply/also/use), collection ops (forEach/map/filter), takeIf/takeUnless,
		// require/check, Result extensions, etc. all land here. Gate on `callNeedsSplice` (AXIS ①): ANY lambda arg emits
		// the OWNER-LESS `callInline` node — bir2cir resolves the hosting file class from the ref.dll [KotlinInline]
		// index (keyed name|pc|ga, disambiguated by a structural `paramSig` match) and splices the raw-BIR body (an
		// extension receiver rides in `recvs.extension`, `with`'s receiver as a regular arg; a noinline lambda rides as a
		// delegate — AXIS ②). There is NO @InlineOnly restriction (a plain `xs.forEach { return }` splices like any
		// other). A lambda-less inline call falls through to the plain callStatic below (the callee's real generic body
		// runs = status quo).
		if (callNeedsSplice(call)) return inlineSpliceCallOwnerless(call, extRecv)
	}
	// Fill omitted default arguments at the call site (IL methods have no default mechanism). ONCE — every branch below
	// shares this ONE list. Filling is not a pure rendering: it lifts a lambda default into the file class, and it binds
	// this call's values into an evaluation plan. Running it a second time for the extension branch therefore rendered
	// every default twice (`"s".ext()` against `fun String.ext(a: Int = bump(), b: Int = a * 10)` called `bump()` twice,
	// where the non-extension `plain()` called it once). It also runs BEFORE either receiver is read, so a plan's
	// receiver binding is the one thing both this call node and any spliced default read.
	val regArgs = filledArgs(call)
	val args = regArgs.joinToString(",")
	// A generic method `fun <T> id(...)` -> carry the resolved type args so ilemit can MakeGenericMethod.
	val ta = typeArgsJson(call)
	// PLAIN Kotlin return type for the retType hint; a `suspend` callee is flagged by `suspendCallTag` on the node
	// (the kickoff/Task/await lowering is a deferred downstream layer). kotc bakes no coroutine ABI here.
	val effRet = birType(call.type)
	val recv = dispatchReceiver(call)
	// Kotlin 2.4 KLIB metadata can carry a genuine static class member
	// (Flags.IS_STATIC_FUNCTION). Preserve only the frontend facts here: the
	// declaring class identity, static-ness, Kotlin name, and declared signature.
	// Whether that identity is a CLR type and which physical member it binds to
	// remains NetInteropBinding's decision in bir2cir.
	if (recv == null && callee.isStaticMethodOfClass) {
		val staticOwner = callee.parent as IrClass
		return """{"k":"callStatic","ownerType":${fqnJson(typeName(staticOwner))},"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$args]${suspendCallTag(callee)}}"""
	}
	// #199 DESIGN B — TWO-AXIS top-level call encoding. `owner:null` is LOAD-BEARING BIR vocabulary meaning "this is
	// a top-level call": ~12 bir2cir recognizers key on it (@ClrIntrinsic/@ClrCollectionFactory/@ClrArrayFactory
	// substitution, Precondition/Repeat/Enum/ForIn/CharSeq lowerings, …). So a same-module top-level call KEEPS
	// `owner:null` (the substitution/recognition axis — UNTOUCHED) and instead carries `calleeOwner`, the
	// FIR-resolved callee file-class (the mandatory DISPATCH axis — the owner-null recognition machinery IGNORES it, while
	// ilemit's dispatch consults it, mirroring `sty`). That disambiguates two same-simple-name top-level funcs in
	// DIFFERENT packages (a.foo/b.foo both emit `method:foo`) without shadowing substitution. See `calleeOwnerTag`.
	// An extension function: the receiver is the `__self` first arg. TOP-LEVEL `fun T.f()` -> static `f(self,args)`.
	// MEMBER `class C { fun T.f() }` has BOTH receivers -> instance method on the enclosing C (dispatch receiver),
	// with the extension receiver as the first arg (mirrors the JVM `C.f(T $receiver)` shape).
	val extRecv = extensionReceiver(call)
	if (extRecv != null) {
		val all = (listOf(expr(extRecv)) + regArgs).joinToString(",")
		if (recv != null) {
			val ownerStr = ownerSpec(declaringClass, recv.type)
			val virtual = isVirtualInstanceCall(call, callee)
			return """{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${expr(recv)},"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$all]${suspendCallTag(callee)}${superTag(call)}}"""
		}
		return """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$all]${suspendCallTag(callee)}${calleeOwnerTag(callee)}}"""
	}
	// Instance method on a user class, or a sibling top-level call.
	return if (recv != null) {
		// Render the receiver before deriving its owner TypeSpec. An object-literal receiver materializes its lifted
		// class at this point and records any enclosing generic parameters captured by that class. Deriving ownerSpec
		// first sees neither fact and emits an open `dotkt$obj::member` call beside `new dotkt$obj<T>`.
		val renderedRecv = expr(recv)
		// `it.hasNext()`/`it.next()` on a Kotlin iterator, `xs.iterator()` on a Kotlin iterable dispatch on the REAL
		// generic identity via ownerSpec below (`kotlin.collections.Iterator[int]` / `Iterable[int]`) — bir2cir
		// substitutes/normalizes them (no monomorphized synthetic; #58).
		val ownerStr = ownerSpec(declaringClass, recv.type)
		val virtual = isVirtualInstanceCall(call, callee)
		// An override of kotlin.Any's universal method (toString/equals/hashCode) carries `anySlot:true` — a pure-
		// Kotlin fact; bir2cir renames it to the System.Object slot. The Kotlin<->CLR name binding for any other
		// interface member is bir2cir's too.
		val mname = name
		val anySlotTag = if (isAnySlotMethod(callee)) ""","anySlot":true""" else ""
		// Carry the frontend-resolved result type of the call. It is the last of the three result-type stamps
		// (`sty`, then `ret`, then `dynRet` — bir-common/NodeType.cs owns that precedence), and downstream passes read
		// it wherever a node has to state its own result. It was introduced to feed ilemit's runtime-reflection
		// fallback, which no longer exists (#400); the stamp itself is ordinary frontend vocabulary and stays.
		val dynRet = ""","dynRet":${birType(call.type).toJson()}"""
		"""{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${recvExpr(recv, ownerStr, declaringClass?.defaultType, renderedRecv)},"method":${str(mname)}${overloadSigField(callee)}$ta$dynRet${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$args]${suspendCallTag(callee)}${overridesJson(callee)}$anySlotTag${superTag(call)}}"""
	} else """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$args]${suspendCallTag(callee)}${calleeOwnerTag(callee)}$companionExtensionCallTag}"""
}

/**
 * The PLAIN `callStatic` node for a call to a top-level function restored from a `[KotlinFile]` facade on a
 * referenced assembly (owner = the .NET file-facade type; bir2cir's NetInteropBinding shapes it to
 * clrStatic / clrGenericStatic). This is the fall-through for a lambda-less external inline call (the callee is a
 * real generic method the JIT inlines) as well as every ordinary non-inline top-level fun.
 */
internal fun BirEmitter.plainExternalTopLevelCall(call: IrCall, callee: IrSimpleFunction, fileClass: String, name: String, extRecv: IrExpression?): String {
	val companionExtensionCallTag = companionReceiverCallTag(callee, call)
	val companionExtension = isCompanionExtensionCallee(callee) || companionExtensionCallTag.isNotEmpty()
	val extParam = extensionReceiverParam(callee)
	// A lazy cross-module companion extension can retain a phantom receiver expression even though its declaration
	// has no physical receiver parameter. Argument and signature shape follow the declaration fact exclusively.
	val physicalExtRecv = extRecv.takeIf { !companionExtension && extParam != null }
	// A GENERIC top-level fun (e.g. a `reified` inline restored as a generic method) -> a generic static
	// call carrying the type args, so ilemit MakeGenericMethods it (the reified `typeof(T)`/`is T` body
	// then sees the concrete type). CLR generics are reified, so no inlining is needed across assemblies.
	if (callee.typeParameters.isNotEmpty()) {
		val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
		if (targs.all { it != null }) {
			// An extension fun: its receiver is the .NET method's first param (`__self`), so prepend it to the args.
			// Keep external args as BIR strings: a non-constant cross-module default has no honest IrExpression and is
			// represented by a positional `defaultArg` for bir2cir to splice from `[KotlinDefault]`.
			// FILL FIRST, then read the extension receiver (the fill binds it under an evaluation plan).
			val externalRegArgs = filledExternalArgs(call, companionExtension)
			val a = listOfNotNull(physicalExtRecv?.let { expr(it) }) + externalRegArgs
			val taJson = targs.joinToString(",") { birType(it!!).toJson() }
			// `shapeTypes` must line up with `a` (= extension receiver, then regular args), so a GENERIC extension
			// fun's `__self` receiver type is included — else bir2cir's by-shape overload pick finds 0 params.
			// PURE-KOTLIN `birType` identities; bir2cir derives the ilemit `shapes` tokens (see the member path above).
			val shapeParams = listOfNotNull(extParam) + regularParams(callee)
			val shapeTypes = shapeParams.joinToString(",") { birType(it.type).toJson() }
			// A2 (#61): a PLAIN static call by identity carrying the generic facts (typeArgs + shapeTypes);
			// bir2cir's NetInteropBinding resolves the file-class owner off the refs and shapes it to clrGenericStatic.
			return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(name)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[${a.joinToString(",")}]${suspendCallTag(callee)}$companionExtensionCallTag}"""
		}
	}
	// A2 (#61): a PLAIN static call by identity to the referenced .NET file class; bir2cir's NetInteropBinding
	// shapes it to clrStatic. A `suspend` callee is flagged by `suspendCallTag` (Task/await lowering deferred).
	val ret = birType(callee.returnType)
	// #146: build the regular args as STRINGS so an omitted NON-CONST default emits a `defaultArg` placeholder (bir2cir's
	// DefaultArgSplice fills it from the callee's ref.dll @KotlinDefault). The extension receiver (arg[0] = `__self`) is
	// prepended; each arg's type is its PARAMETER's type (a placeholder carries no expr type). `sig` (the callee's full
	// .NET signature) drives DefaultArgSplice's arg-count match against the ref.dll @KotlinDefault key.
	val regArgs = filledExternalArgs(call, companionExtension)
	val extStr = physicalExtRecv?.let { expr(it) }
	val argStrs = (listOfNotNull(extStr) + regArgs).joinToString(",")
	val extParamType = extParam?.let { birType(it.type) }
	val argTypeNodes = (listOfNotNull(extParamType) + regularParams(callee).map { birType(it.type) }.take(regArgs.size)).joinToString(",") { it.toJson() }
	return """{"k":"callStatic","ownerType":${fqnJson(fileClass)},"method":${str(name)}${overloadSigField(callee)},"argTypes":[$argTypeNodes],"ret":${str(ret)},"args":[$argStrs]${suspendCallTag(callee)}$companionExtensionCallTag}"""
}

/**
 * `,"ret":${fqnJson("kotlin.Int")}` for a generic call/member access: the concrete result type is known here (FIR-resolved
 * `call.type`), so ilemit need not reflect the un-baked builder's return type (which stays `!0`/`!!0` and
 * would mis-drive value-type boxing). Only emitted for the generic/constructed paths to stay non-invasive.
 */
internal fun BirEmitter.retHint(generic: Boolean, t: IrType): String =
	if (generic) ""","ret":${birType(t).toJson()}""" else ""

/** Like [retHint] but with a pre-computed return-type string (e.g. a suspend call's kickoff `Task<T>`). */
internal fun BirEmitter.retHintStr(generic: Boolean, ret: TypeNode): String =
	if (generic) ""","ret":${ret.toJson()}""" else ""

/** Neutral metadata tag marking a call whose callee is a `suspend` function. kotc records only the FACT
 *  (mirroring the `"suspend":true` fn-decl flag); the coroutine LOWERING (await / state machine / Task ABI)
 *  is a DEFERRED downstream layer that consumes this tag. kotc does NO coroutine lowering. */
internal fun BirEmitter.suspendCallTag(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
	if ((callee as? IrSimpleFunction)?.isSuspend == true) {
		val awaitBridge = callee.annotations.any {
			it.type.classFqName?.asString() == "kotlin.clr.ClrAwaitBridge"
		}
		""","suspendCall":true${if (awaitBridge) ""","clrAwaitBridge":true,"awaitResult":${birType(callee.returnType).toJson()}""" else ""}"""
	} else ""

/** #199/#204: the `,"calleeOwner":<fileClassFqn>` mandatory DISPATCH identity on a top-level `callStatic` whose `owner` stays
 *  `null`. `owner:null` is the load-bearing "top-level call" axis ~12 bir2cir owner-null recognizers key on
 *  (@ClrIntrinsic/@ClrCollectionFactory/@ClrArrayFactory substitution, Precondition/Repeat/Enum/ForIn/CharSeq
 *  lowerings, …); calleeOwner is a SEPARATE axis those passes ignore (they carry it through DeepClone or
 *  legitimately drop it when replacing a recognized call). ONLY ilemit's callStatic dispatch consults it — mirroring
 *  `sty`, a frontend-resolved per-node fact consumed downstream without re-resolution. Same-module declarations use
 *  their real IrFile; reference-KLIB cross-module functions use their projected file class. Other cross-module calls
 *  may omit it only while still BIR owner:null: bir2cir must replace them with an explicit owner before the CIR sanity
 *  boundary. `decl` is the callee function (or, for a top-level
 *  extension property accessor, the property itself — its file class holds the static accessor). */
internal fun BirEmitter.calleeOwnerTag(decl: org.jetbrains.kotlin.ir.declarations.IrDeclaration): String {
	val owner = (decl as? org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer)?.let { clrExternalOwner(it) }
		?: if (decl.parent is IrFile) fileClassOf(decl) else null
	return owner?.let { ""","calleeOwner":${fqnJson(it)}""" } ?: ""
}

/** The owner of a static lift synthesized into the file currently being emitted. Unlike [calleeOwnerTag], this is
 *  never optional: the producer created the target method itself, so its exact file-class identity is known. */
internal fun BirEmitter.localCalleeOwnerTag(): String = ""","calleeOwner":${fqnJson(fileClass)}"""

/** `,"super":true` on a `super.X()` callInstance (issue #14). kotc already forces `virtual:false` here
 *  (isVirtualInstanceCall), but that non-virtual intent is LOST when a CLR-binding pass in bir2cir reshapes the
 *  node to a `clrInstance`/`clrPropGet` (NetInteropBinding / MemberCallSubstitution drop the `virtual` field). This
 *  marker RIDES ALONG so those passes can re-stamp the produced CLR node non-virtual, and ilemit emits `call`
 *  (not `callvirt`) for a reference owner — a base-slot dispatch exactly like C#'s `base.M()`. Without it a super
 *  call to a CLR-bound base (kotlin.Any/System.Object, a dll2klib-projected .NET base, a @ClrTypeAlias stdlib base)
 *  callvirt-re-dispatches to THIS class's override -> infinite recursion. */
internal fun BirEmitter.superTag(call: IrCall): String =
	if (call.superQualifierSymbol != null) ""","super":true""" else ""

/** `,"typeArgs":["int"]` when the callee is a generic method (its own type params resolved at this call). */
internal fun BirEmitter.typeArgsJson(call: IrCall): String {
	// Read the resolved arguments from the call itself. Kotlin 2.4 represents a same-module companion-block static
	// call through a wrapper symbol whose own typeParameters list is empty even though the IrCall retains the real
	// TYPE_ARG entries. Gating on the wrapper declaration therefore silently dropped method instantiation facts.
	val args = call.typeArguments
	if (args.isEmpty()) return ""
	if (args.any { it == null }) return ""
	return ""","typeArgs":[${args.joinToString(",") { birType(it!!).toJson() }}]"""
}

/** The `byref(x)` marker intrinsic wrapping an arg -> the inner lvalue `x`; else null. Matched by FULL name
 *  (`kotlin.clr.byref`) so a user function happening to be named `byref` is not mistaken for the intrinsic. */
internal fun BirEmitter.byrefMarker(a: IrExpression): IrExpression? =
	if (a is IrCall && a.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.clr.byref") regularArgs(a).firstOrNull() else null

/** A stdlib byref parameter marked `@kotlin.clr.ClrRefArgument`: its argument is passed BY REFERENCE to the bound
 *  BCL member (bir2cir wraps the arg position `byref:` at substitution). kotc reads it ONLY to SHAPE the argument
 *  addressably — the byref call-substitution decision itself is bir2cir's. */
internal fun BirEmitter.isClrRefArgument(p: IrValueParameter): Boolean =
	p.annotations.any { it.type.classFqName?.asString() == "kotlin.clr.ClrRefArgument" }

/** The ADDRESSABLE lvalue a byref call slot takes, or null when the slot takes an ordinary copied VALUE. Two byref
 *  shapes: a USER `ClrRef<T>` param (`byref:`) unwraps its explicit `byref(x)` marker; a STDLIB `@ClrRefArgument`
 *  param (a PLAIN type, no marker) shapes the bare arg directly. An address is not a value: no storage can hold it, so
 *  a call-evaluation plan records it as an `address` binding — an ordering marker — rather than as a bound value.
 *  THE single source of truth for "is this slot an address": [argExpr] renders it and [filledArgs] classifies by it. */
internal fun BirEmitter.addressSlotExpr(arg: IrExpression, param: IrValueParameter?): String? {
	if (param == null) return null
	if (birType(param.type) is TypeNode.ByRef) return byrefMarker(arg)?.let { inner -> byrefBackingField(inner) ?: expr(inner) }
	if (isClrRefArgument(param)) return byrefBackingField(arg) ?: expr(arg)
	return null
}

/** Emit one regular call argument as its ADDRESSABLE lvalue (a property's backing FIELD node, else the lvalue
 *  itself) when the matching callee parameter is byref, so ilemit's EmitArg(want.IsByRef) can `ldflda`/`ldloca` it.
 *  Two byref shapes: a USER `ClrRef<T>` param (`byref:`) unwraps its explicit `byref(x)` marker; a STDLIB
 *  `@ClrRefArgument` param (a PLAIN type, no marker) shapes the bare arg directly — the stdlib's @ClrIntrinsic
 *  Interlocked/TryParse/DivRem helpers, plain calls in the ref build, substituted to BCL `ref`/`out` calls by
 *  bir2cir in the rt build. A non-byref parameter is unaffected (inert for every existing call). */
internal fun BirEmitter.argExpr(arg: IrExpression, param: IrValueParameter?): String {
	addressSlotExpr(arg, param)?.let { return it }
	if (param != null) {
		// A value-type-nullable arg (`Int?` smart-cast to `Int`) passed to a non-null value param must UNWRAP
		// `Nullable<T>.Value` — the CLR twin of JVM's implicit `Integer.intValue()` arg coercion (no IR node). C1.
		if (!isPreUnwrappedRead(arg)) nullableValueUnwrapElem(arg.type, param.type)?.let { elem ->
			return """{"k":"nullableValue","elem":${str(elem)},"e":${expr(arg)}}"""
		}
		// A boxed Any operand (an un-narrowed smart-cast, `x is Int && f(x)`) passed to a concrete value-primitive
		// param -> cast to the param type so the VALUE, not the box, reaches the slot. This is the arg twin of
		// recvExpr's boxed-Any coercion: a primitive operator (`a + b`) lowered by bir2cir flows its arg through here.
		if (param.type.isPrimitiveOrUnsigned() && birType(arg.type) == OBJ)
			return """{"k":"cast","type":${str(birType(param.type))},"e":${expr(arg)}}"""
	}
	return expr(arg)
}

/** Read the RECEIVER of a member call on a value-type primitive as its BARE VALUE: a value-nullable (`Int?`)
 *  smart-cast surfaces `Nullable<T>.Value`; a boxed `Any` smart-cast casts to the primitive. The receiver-slot
 *  twin of [argExpr]'s value coercion — a member call on `kotlin.Int`/`kotlin.Char`/… (a primitive
 *  operator, `compareTo`, `toString`, …) needs the raw value, not a `Nullable<T>` struct load / a box. A no-op
 *  for any non-primitive owner. */
internal fun BirEmitter.recvExpr(recv: IrExpression, ownerType: TypeNode, ownerIr: IrType?, rendered: String? = null): String {
	fun value() = rendered ?: expr(recv)
	// The owner's value-primitive-ness is read from the IR (`ownerIr` = the member's declaring class, or the
	// receiver's own type when the receiver was boxed to Any) — no kotlin.* primitive FQN table.
	val ownerPrim = ownerIr?.isPrimitiveOrUnsigned() == true || recv.type.isPrimitiveOrUnsigned()
	if (!ownerPrim || isPreUnwrappedRead(recv)) return value()
	nullableElem(recv.type)?.let { elem -> return """{"k":"nullableValue","elem":${str(elem)},"e":${value()}}""" }
	if (birType(recv.type) == OBJ) return """{"k":"cast","type":${str(ownerType)},"e":${value()}}"""
	return value()
}

/** A `byref(...)` target that is an own-source-set property read -> its BACKING-FIELD node, so ilemit takes the
 *  field address (`ldflda <backing>`) instead of addressing an accessor's return value (Phase 5). The field is
 *  private when it is accessor-routed. Preserve the frontend declaration fact on the address node; bir2cir decides
 *  whether its physical owner differs and needs an UnsafeAccessor. Null for a non-property, a .NET/external property,
 *  or a computed/delegated/lateinit/@ClrField property (no plain in-module backing field to address). */
internal fun BirEmitter.byrefBackingField(inner: IrExpression): String? {
	val call = inner as? IrCall ?: return null
	val callee = call.symbol.owner
	val prop = callee.correspondingPropertySymbol?.owner ?: return null
	if (callee !== prop.getter) return null
	val cls = callee.parent as? IrClass ?: return null
	if (isExternalNetType(cls)) return null
	val backing = prop.backingField ?: return null
	if (prop.isDelegated || isLateinitProperty(prop) || isClrField(prop)) return null
	val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
	val owner = ownerSpec(cls, dispatchReceiver(call)?.type).toJson()
	val rendered = """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(prop.name.asString())}}"""
	return memberFieldVisibilityStamped(backing, rendered)
}

/** (argsJson, argTypesJson) for an external .NET / restored-DotKt call — the ONE builder every such call site uses,
 *  so the two vectors are always the SAME physical sequence: `[__self?] + contexts + regulars`, positions filled by
 *  [filledExternalArgs].
 *
 *  Both halves derive from the callee's PARAMETERS, never from "the expressions that happen to be present". Building
 *  them from provided expressions (the previous shape here and at the three sibling member paths) silently DELETED an
 *  omitted default's slot: no `defaultArg` placeholder was emitted, so a later provided argument slid into the omitted
 *  parameter's position. `sig` (emitted by the callers via [overloadSigField]) identifies the frontend-selected
 *  declaration; `argTypes` stays aligned with the args actually emitted. bir2cir fills both positional placeholders
 *  and a purely trailing short vector from that declaration's reference-DLL defaults.
 *
 *  A `ClrRef<T>` param already maps to `byref:T` via birType (so the out/ref overload resolves + optional params still
 *  default-fill); a `byref(x)` arg unwraps to its lvalue `x`, which ilemit passes by address. */
internal fun BirEmitter.clrCallArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression, callee: org.jetbrains.kotlin.ir.declarations.IrFunction): Pair<String, String> {
	val aj = filledExternalArgs(call)
	val tj = regularParams(callee).map { birType(it.type).toJson() }.take(aj.size)
	return aj.joinToString(",") to tj.joinToString(",")
}

/** [clrCallArgs] with the callee's EXTENSION receiver prepended to both vectors — a .NET `static M(this T self, …)`
 *  surfaced as a Kotlin extension, or a restored DotKt member extension whose leading `__self` is that receiver. */
internal fun BirEmitter.clrCallArgsWithRecv(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
		callee: org.jetbrains.kotlin.ir.declarations.IrFunction, extRecv: IrExpression): Pair<String, String> {
	val (aj, tj) = clrCallArgs(call, callee)
	val extParamType = extensionReceiverParam(callee)
		?.let { birType(it.type) } ?: birType(extRecv.type)
	val args = listOf(expr(extRecv)) + listOfNotNull(aj.takeIf { it.isNotEmpty() })
	val types = listOf(extParamType.toJson()) + listOfNotNull(tj.takeIf { it.isNotEmpty() })
	return args.joinToString(",") to types.joinToString(",")
}

// #82: whether an IrProperty is backed by a REAL static field vs a COMPUTED property whose cross-module
// deserialized stub carries a phantom backingField. Source IR: backingField is ground truth. A metadata
// (Fir2IrLazyProperty) stub: trust the deserialized FIR accessor kind — Fir2IrLazyProperty materializes a
// spurious IrField for any bodyless custom getter, and keeps IR_EXTERNAL_DECLARATION_STUB origin on BOTH
// default and custom accessors, so getter.origin cannot discriminate; FirDefaultPropertyGetter can.
private fun BirEmitter.hasRealStaticField(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.getter == null || fir.getter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertyGetter
	}
	return prop.backingField != null
}

// #89: whether a property's GETTER is DEFAULT (kotc-generated trivial `field` passthrough). A property may
// have BOTH a real static backing field (an initializer) AND a custom `get() = field + 1` — reading it as a
// raw static-field load would skip the getter (the bug). So a top-level/companion property is only read as a
// static field when it has a real field AND a default getter; a custom getter must be invoked. For a
// same-module source property the accessor origin discriminates; a cross-module Fir2IrLazyProperty stub keeps
// IR_EXTERNAL_DECLARATION_STUB origin on both kinds, so trust the deserialized FIR accessor kind instead.
internal fun BirEmitter.hasDefaultGetter(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.getter == null || fir.getter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertyGetter
	}
	val g = prop.getter ?: return true
	return g.origin == org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin.DEFAULT_PROPERTY_ACCESSOR
}

// #89 (write side): whether a property's SETTER is DEFAULT. A `var x = init; set(v) { field = v.trim() }` has a
// real field AND a custom setter — writing it as a raw static-field store would skip the setter. Symmetric to
// hasDefaultGetter.
internal fun BirEmitter.hasDefaultSetter(prop: IrProperty): Boolean {
	(prop as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.let { fir ->
		return fir.setter == null || fir.setter is org.jetbrains.kotlin.fir.declarations.impl.FirDefaultPropertySetter
	}
	val s = prop.setter ?: return true
	return s.origin == org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin.DEFAULT_PROPERTY_ACCESSOR
}

// #89: a property whose backing field is accessed THROUGH `field`-based get_/set_ accessors — the routing this
// fix targets. `const` is frontend-inlined; `lateinit` keeps a raw null-checked field with default accessors;
// `@ClrField` is a plain field by opt-in. Delegated properties are accessor-routed too: their provider field is
// storage, while the frontend-generated accessor body owns getValue/setValue lowering.
internal fun BirEmitter.fieldRoutedProperty(prop: IrProperty): Boolean =
	!prop.isConst && !isLateinitProperty(prop) && !isClrField(prop)
// Once either accessor needs a method, the top-level property has one accessor-owned surface. Emit both roles (for a
// var) and route both through it; otherwise the default half would remain a public field while the custom half became
// a CLR Property, and the frontend fact that the declaration is mutable could not survive round-trip metadata.
internal fun BirEmitter.accessorRoutedTopLevelProperty(prop: IrProperty): Boolean =
	fieldRoutedProperty(prop) &&
		(prop.getter?.let { !hasDefaultGetter(prop) } == true ||
			prop.setter?.let { !hasDefaultSetter(prop) } == true ||
			// A physical method name requires a MethodDef even when the Kotlin accessor body is default. A
			// field-backed companion extension is different: bir2cir owns that representation and receives the two
			// accessor-name facts on its field declaration instead.
			(companionReceiverJson(prop) == null &&
				(prop.getter?.let { hasExplicitClrNameAnnotation(it) } == true ||
					prop.setter?.let { hasExplicitClrNameAnnotation(it) } == true)) ||
			// A compiler-produced DLL -> KLIB property with a trusted accessor identity represents an existing
			// MethodDef surface even when FIR reconstructs its accessor as default. Keep that surface method-routed so
			// declarationId can bind direct calls and callable references to the exact referenced MethodDef.
			(clrExternalOwner(prop) != null &&
				(prop.getter?.let { declarationIdForPhysicalAllocation(it) } != null ||
					prop.setter?.let { declarationIdForPhysicalAllocation(it) } != null)))
// #89: a property READ resolves to a raw static-field load only with a real field AND (for a field-routed
// property) no accessor-owned surface. An excluded (const/lateinit/@ClrField) property keeps the pre-fix rule; a
// delegated property is field-routed because its provider-typed slot is never the value surface.
internal fun BirEmitter.readsAsStaticField(prop: IrProperty): Boolean =
	hasRealStaticField(prop) && (!fieldRoutedProperty(prop) || !accessorRoutedTopLevelProperty(prop))
// #89: a property WRITE resolves to a raw static-field store only with a real field AND (for a field-routed
// property) no accessor-owned surface.
internal fun BirEmitter.writesAsStaticField(prop: IrProperty): Boolean =
	hasRealStaticField(prop) && (!fieldRoutedProperty(prop) || !accessorRoutedTopLevelProperty(prop))

/** Records that a LOCAL delegated-property access was rendered as the delegate member (locals have no CLR accessor). */
internal fun BirEmitter.delegateInlined(
	call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	rendered: String,
	target: org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility? = null,
): String {
	delegateInlinedAccess = call to target
	return rendered
}
