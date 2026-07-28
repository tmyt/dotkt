package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
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
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isNothing
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

// BIR expression rendering: an IrExpression -> a BIR JSON node (extension on BirEmitter).
//
// #122: stamp the FRONTEND-RESOLVED static type `sty` (the instantiated `node.type`, incl. smart-cast refinement,
// generic args and nullability) at this single chokepoint. bir2cir's StaticType CONSUMES it — reading an operand's
// Kotlin static type off `sty` — instead of RE-deriving a callee's return type by re-doing overload resolution
// against the ref.dll (the no-re-resolution-downstream invariant). Stamped ONLY on the value-node kinds StaticType
// reads a return/static type from (`local`, `callStatic`, `callInstance`, `field`, `lateinitGet`, `staticField`);
// the STRUCTURAL kinds (cast/const/new/conv/arrayGet/…) already carry their own type slot, so they need no stamp.
// A pass-through arm (e.g. coercion-to-Unit returning its already-stamped argument) begins with `{"sty":` — not a
// bare `{"k":<kind>` — so the prefix guard skips it and there is no double-stamp.
internal fun BirEmitter.expr(node: IrExpression): String {
	// A value the ENCLOSING call bound to a temp for single evaluation: every reader renders as that temp's read.
	evalOnceSubst[node]?.let { return styStamped(node, it) }
	val (temps, s) = withCallValuesBoundOnce(node) { styStamped(node, exprInner(node)) }
	if (temps.isEmpty()) return s
	return """{"k":"valueBlock","type":${birType(node.type).toJson()},"stmts":[${temps.joinToString(",")}],"result":$s}"""
}

/** #235: run `emit` with every value of `call` that a filled default SPLICES bound to a temp local, and hand back the
 *  `var` statements that declare them (in evaluation order) for the caller to place. THE call sites that are not an
 *  expression — a constructor DELEGATION and an ENUM ENTRY — place them differently from [expr]'s `valueBlock`, but the
 *  binding itself is identical, so it lives here once. Empty list ⇒ nothing needed binding and `emit`'s output stands
 *  alone. */
internal fun <T> BirEmitter.withCallValuesBoundOnce(call: IrExpression, emit: () -> T): Pair<List<String>, T> {
	val hoists = hoistCallValuesReadByDefaults(call)
	val temps = hoists.mapTo(ArrayList()) { (order, _, stmt) -> order to stmt }
	val previous = callEvalOnceTemps.put(call, temps)
	val out = try { emit() } finally {
		hoists.forEach { (_, value, _) -> evalOnceSubst.remove(value) }
		if (previous != null) callEvalOnceTemps[call] = previous else callEvalOnceTemps.remove(call)
	}
	return temps.sortedBy { it.first }.map { it.second } to out
}

/** #122's `sty` stamp on the value-node kinds bir2cir's StaticType reads a type from (see the note above). */
private fun BirEmitter.styStamped(node: IrExpression, s: String): String =
	if (styNodePrefixes.any { s.startsWith(it) }) """{"sty":${birType(node.type).toJson()},${s.substring(1)}""" else s

/** #235: bind each value of THIS call that a filled default SPLICES — the receiver (or enclosing instance) a
 *  `= this` / `= outerProp` default reads, and each provided ARGUMENT an omitted default reads (`b: Int = a * 10`) —
 *  to a temp local, so it is evaluated EXACTLY ONCE however many times it is spliced. Kotlin evaluates a receiver and
 *  each argument once; splicing the rendered expression per reader would not.
 *
 *  Hoisting REORDERS: a temp's initializer runs before the call node, so every non-stable value to the LEFT of the last
 *  hoisted one is bound too, even when no default reads it — otherwise it would slide after a value Kotlin evaluates
 *  later (`g(a(), b())` with `r = q * 10` must still run `a()` first). Returns (parameter order, IR node, `var`
 *  statement), where parameter order = receivers then arguments left to right, for the caller to merge with temps that
 *  [filledArgs] adds after rendering an omitted default. Every reader (the call's own receiver/argument slot, an
 *  inner-class `new`'s enclosing-instance argument, the spliced default) reaches the value through `expr()` on the SAME
 *  IR node, so [BirEmitter.evalOnceSubst] hands them all the one temp.
 *  Empty when there is nothing to bind: not a call, no omitted default this call FILLS, no default that reads a call
 *  value, or only STABLE values (a literal / immutable local or parameter read — free to re-read and impossible to
 *  observe out of order, exactly [bindOnce]'s test). */
private fun BirEmitter.hoistCallValuesReadByDefaults(node: IrExpression): List<Triple<Int, IrExpression, String>> {
	val call = node as? org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression ?: return emptyList()
	val callee = call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction ?: return emptyList()
	if (callee.parameters.none { it.kind == IrParameterKind.Regular && it.defaultValue != null }) return emptyList()
	// A SOURCE-SPLICED inline call fills nothing here (its omitted default rides the inline carrier and its lambda args
	// are spliced, not emitted as values), so a temp would be dead — and emitting a lambda into one lifts it twice.
	if (isInlineSplicedCall(call)) return emptyList()
	// The defaults this call actually fills (a cross-module IrErrorExpression carries no readable expression: that
	// omission becomes a positional placeholder bir2cir fills, and nothing of this call is spliced into it here).
	val omitted = callee.parameters.withIndex().mapNotNull { (i, p) ->
		if (p.kind != IrParameterKind.Regular || (i < call.arguments.size && call.arguments[i] != null)) null
		else p.defaultValue?.expression?.takeIf { it !is org.jetbrains.kotlin.ir.expressions.IrErrorExpression }
	}
	if (omitted.isEmpty()) return emptyList()
	// The receiver `filledArgs` splices is whichever of the two this call has: the extension receiver if any, else the
	// dispatch receiver. Reads of ANY enclosing `this` render as that same expression (an inner-class ctor / member).
	val recvIdx = callee.parameters.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		.takeIf { it >= 0 } ?: callee.parameters.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
	val enclosingSyms = enclosingThisChain(callee).map { it.first.symbol }
	// Does a filled default read the value passed at parameter position `i`?
	fun readsAt(i: Int): Boolean {
		val syms = HashSet<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>()
		callee.parameters.getOrNull(i)?.let { syms.add(it.symbol) }
		if (i == recvIdx) syms.addAll(enclosingSyms)
		return syms.isNotEmpty() && omitted.any { refsAny(it, syms) }
	}
	val values = callee.parameters.indices.mapNotNull { i ->
		(if (i < call.arguments.size) call.arguments[i] else null)?.let { i to it }
	}
	val last = values.lastOrNull { (i, _) -> readsAt(i) }?.first ?: return emptyList()
	val out = ArrayList<Triple<Int, IrExpression, String>>()
	for ((i, value) in values) {
		if (i > last) break
		val p = callee.parameters[i]
		// A byref / @ClrRefArgument arg is emitted as an ADDRESSABLE lvalue, never through `expr()` on this node, so a
		// temp would be dead. An lvalue read is side-effect-free anyway, so leaving it in place reorders nothing.
		if (birType(p.type) is TypeNode.ByRef || isClrRefArgument(p)) continue
		if (evalOnceSubst.containsKey(value) || isStableValue(value)) continue
		// `dotkt$` NAMESPACE, not `__recv`/`__arg`. These call-site temps are minted from `scopeCounter` while a body
		// is being emitted, and the emitted FRAME may already hold a name minted from the OTHER counter
		// (`BirEmitter.freshFrameName` allocates a lifted lambda's receiver parameter as `__recv<inlCounter>` before
		// this body runs). Two counters over one prefix can produce one name: ilemit registers a local before emitting
		// its initializer and resolves locals ahead of arguments, so the initializer read its own zero-initialized
		// local instead of the receiver — `{ normalize(this).pick() }` on `Box(7)` yielded 99.
		// Disjointness here is by CONSTRUCTION rather than by a check: `dotkt$…` and `__…` cannot be equal for any
		// counter values, so neither site has to know the other exists. It is the same namespace ordinary locals are
		// already renamed into (`dotkt$localN`), and `$` is not writable in a plain Kotlin identifier, so it also
		// cannot alias a name the USER chose.
		val tv = if (i == recvIdx) "dotkt\$recv${scopeCounter++}" else "dotkt\$arg${scopeCounter++}"
		out.add(Triple(i, value, """{"k":"var","name":${str(tv)},"type":${birType(value.type).toJson()},"init":${expr(value)}}"""))
		evalOnceSubst[value] = """{"k":"local","name":${str(tv)}}"""
	}
	return out
}

/** Bind an OMITTED default after [filledArgs] has rendered it in the callee's substituted scope. The ordinary pre-pass
 *  can bind only expressions present in `call.arguments`; a default absent from that array must join the same ordered
 *  temp list here when a later filled default reads its parameter. Returning the local makes both the call's own slot and
 *  every later symbolic substitution read the one value. */
internal fun BirEmitter.bindFilledDefaultOnce(
	call: IrExpression,
	paramIndex: Int,
	param: IrValueParameter,
	defaultExpr: IrExpression,
	emitted: String,
): String {
	val temps = callEvalOnceTemps[call] ?: return emitted
	if (isStableValue(defaultExpr)) return emitted
	if (birType(param.type) is TypeNode.ByRef)
		return unsupported(call, "omitting a by-reference default argument that another default reads",
			"the default value of parameter '${param.name.asString()}' cannot be copied into a temporary; pass it explicitly")
	// Same `dotkt$` namespace as the pre-pass temps above (see the note there): minted from `scopeCounter` into a
	// frame that may already hold an `inlCounter`-minted name.
	val tv = "dotkt\$arg${scopeCounter++}"
	temps.add(paramIndex to """{"k":"var","name":${str(tv)},"type":${birType(param.type).toJson()},"init":$emitted}""")
	return """{"k":"local","name":${str(tv)}}"""
}

/** [bindOnce]'s stability test: a const or a read of an immutable non-ref-cell local/parameter re-reads for free and
 *  without side effects, so splicing it twice is safe and it needs no temp. */
private fun BirEmitter.isStableValue(e: IrExpression): Boolean =
	e is IrConst || (e as? IrGetValue)?.symbol?.owner?.let { o ->
		!isRefCell(o) && (o is IrValueParameter || (o as? IrVariable)?.isVar == false)
	} == true

private val styNodePrefixes = listOf(
	"""{"k":"local"""", """{"k":"callStatic"""", """{"k":"callInstance"""",
	"""{"k":"field"""", """{"k":"lateinitGet"""", """{"k":"staticField"""",
)

internal fun BirEmitter.exprInner(node: IrExpression): String = when (node) {
	is IrConst -> """{"k":"const","type":${birType(node.type).toJson()},"value":${constJson(node)}}"""
	is IrGetValue -> {
		val owner = node.symbol.owner
		val name = owner.name.asString()
		when {
			// A ref-cell var read `x` -> `x.v` (the heap cell, reached via the capture field inside a closure). The cell
			// field `v` holds the FULL element type (`owner.type`); when that is a value-type nullable (`Int?` = `Nullable<T>`)
			// read at a use-site narrowed to the bare value (an inline-closure smart-cast `if (q != null) … q …`, whose
			// IrGetValue.type is the bare `Int`), UNWRAP `Nullable<T>.Value` — mirroring the plain-local read arm below, and
			// keyed on the cell element type `owner.type` (NOT the smart-cast-narrowed `node.type`, which alone would defeat
			// the leaf coerceValue). Consumed as `Nullable<T>` (no narrowing) -> the raw field. (#36)
			isRefCell(owner) -> {
				val raw = """{"k":"field","ownerType":${fqnJson(refTypeName(owner))},"recv":${refBase(owner)},"name":"v"}"""
				val vElem = nullableValueUnwrapElem(owner.type, node.type)
				if (vElem != null) """{"k":"nullableValue","elem":${vElem.toJson()},"e":$raw}""" else raw
			}
			captureSubst.containsKey(owner) -> captureSubst[owner]!!
			selfSubst.containsKey(owner) -> selfSubst[owner]!!   // extension `__self` (by identity, before name-based `<this>`)
			valSubst.containsKey(name) -> valSubst[name]!!
			name == "<this>" -> """{"k":"this"}"""
			else -> {
				val slot = localSlotName(owner)
				// Smart-cast narrowing carried directly on the IrGetValue (no IMPLICIT_CAST node — e.g. the `&&`
				// RHS / a compound condition: `x is Int && x > 10`): the use-site type is narrower than the
				// declared type, so emit a cast (ilemit unboxes Any->Int / castclass for refs). Without it the
				// value keeps its boxed/declared form and ops like `>` compare the wrong thing.
				val ut = birType(node.type); val dt = birType(owner.type)
				// A value-type-nullable (`Int?` = `Nullable<T>`) narrowed to its non-null value (`if (n != null) { …n… }`)
				// must UNWRAP `Nullable<T>.Value` — a bare `local` load of a `Nullable<int>` into an `int` context is
				// invalid IL / reads garbage (the C1 smart-cast miscompile). This is the twin of the IMPLICIT_CAST path.
				val vElem = nullableValueUnwrapElem(owner.type, node.type)
				// The declared type is the boxed Any token ("object" fallback, or "kotlin.Any" for an Any/Nothing source type).
				if (vElem != null) """{"k":"nullableValue","elem":${vElem.toJson()},"e":{"k":"local","name":${str(slot)}}}"""
				else if (ut != dt && dt == OBJ) """{"k":"cast","type":${ut.toJson()},"e":{"k":"local","name":${str(slot)}}}"""
				else """{"k":"local","name":${str(slot)}}"""
			}
		}
	}
	is IrGetEnumValue -> {
		val entry = node.symbol.owner
		val parent = entry.parent as? IrClass
		// Rich enum -> the static singleton field; basic enum -> ordinal const typed as the CLR enum.
		if (parent != null && isRichEnum(parent))
			"""{"k":"staticField","ownerType":${fqnJson(typeName(parent))},"name":${str(entry.name.asString())}}"""
		else {
			val ord = parent?.declarations?.filterIsInstance<IrEnumEntry>()?.indexOf(entry) ?: 0
			"""{"k":"enumValue","type":${fqnJson(parent?.let { typeName(it) } ?: "kotlin.Any")},"ordinal":$ord}"""
		}
	}
	// `object Foo` reference -> load the singleton `Foo.INSTANCE` static field (item 10). (.NET-injected objects
	// like Math are static call sites handled at the call site; only user singletons reach here as a value.)
	// The `Unit` object as a VALUE (e.g. `Result.success(Unit)`) is just another singleton: the stdlib's own
	// `kotlin.Unit` object INSTANCE (this-assembly under stdlib-compile, else resolved against the referenced
	// stdlib) — no DotKt.Runtime.
	is IrGetObjectValue ->
		"""{"k":"staticField","ownerType":${fqnJson(typeName(node.symbol.owner))},"name":"INSTANCE"}"""
	is IrBlock -> blockExpr(node)
	is IrGetField -> {
		val staticOwner = staticBackingFieldOwner(node.symbol.owner)
		val ownerClass = node.symbol.owner.parent as? IrClass
		val clr = ownerClass?.let { clrName(it) }
		val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
		val fldName = node.symbol.owner.name.asString()
		// #89: a STATIC backing field (top-level property -> file class; companion property -> enclosing class) ->
		// a `staticField` load with NO receiver. Reached from the property's OWN custom accessor body reading
		// `field`; a plain field-only property is read directly at the call site (BirEmitterCalls).
		if (staticOwner != null)
			"""{"k":"staticField","ownerType":${fqnJson(staticOwner)},"name":${str(fldName)}}"""
		// `Throwable.message`/`.cause` are PLAIN Kotlin properties: an app read is an IrCall(get_message) routed by
		// bir2cir to clrPropGet System.Exception.Message off the @ClrProperty binding (layer purity — no BCL member name
		// in kotc). A direct backing-FIELD read reaching here is only kotlin.Throwable's own generated getter body in the
		// stdlib ref build, where `message` is a real field — the plain `field` path below serves it.
		else if (clr != null)
			"""{"k":"field","ownerType":${fqnJson(clr)},"recv":$recvJson,"name":${str(fldName)}}"""
		// A `lateinit var` backing-field read -> throw if still uninitialized (null) — proper lateinit semantics.
		else if (node.symbol.owner.correspondingPropertySymbol?.owner?.isLateinit == true)
			"""{"k":"lateinitGet","ownerType":${ownerSpec(ownerClass, node.receiver?.type).toJson()},"recv":$recvJson,"name":${str(fldName)}}"""
		else
			"""{"k":"field","ownerType":${ownerSpec(ownerClass, node.receiver?.type).toJson()},"recv":$recvJson,"name":${str(fldName)}}"""
	}
	is IrConstructorCall -> {
		val klass = node.symbol.owner.parent as? IrClass
		// The GENERIC object array `Array<E>(size){init}` / `Array<E>(size)` -> a real BCL array (newarr + fill loop):
		// the element is the CLR type of E (`Array<Any?>` -> object[]), so a concrete E works (a bare type-param E
		// rides its `gp:E` form). Without this it fell through to a bogus `new kotlin.Array(...)` (wrong-sized array).
		// The SIGNED primitive array ctor (`IntArray(size){init}`) is NOT decomposed here: kotc emits the faithful
		// `new kotlin.IntArray(size, init)` ctor call (the normal-new fall-through below) and bir2cir DERIVES the
		// newArrayInit/newArraySized construction off the faithful `kotlin.IntArray` identity + its element.
		val arrElem: TypeNode? =
			if (klass?.fqNameWhenAvailable?.asString() == "kotlin.Array") {
				val elemType = (((node.type as? IrSimpleType)?.arguments?.firstOrNull()) as? IrTypeProjection)?.type
				// Only a CONCRETE element type routes to a real BCL newarr (`Array<Any?>` -> object[]). A bare TYPE-PARAM
				// element (`Array<T>`) needs reified allocation; routing it here would newarr a `tv` AND make its init
				// `Func<int,T>` a TypeBuilderInstantiation (ilemit GetMethod("Invoke") fails) -> leave it to the fall-through.
				if (elemType != null && elemType.classifierOrNull !is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol) birType(elemType) else null
			} else null
		// An inner-class ctor takes the enclosing instance (its dispatch receiver) as a leading arg. Emitted ONCE, and
		// handed to `filledArgs` as well, so a default that reads the enclosing instance splices the SAME string rather
		// than a second emission of the expression (which would append a second copy of any lifted lambda in it).
		val outerArgLazy = lazy {
			if ((node.symbol.owner.parent as? IrClass)?.isInner == true) dispatchReceiver(node)?.let { expr(it) } else null
		}
		// The ctor's regular args, omitted defaults filled — the SAME single pass every call shape uses. Emitted once
		// (`by lazy`, at the first branch that needs it): re-running it would duplicate any lift/lambda emission side
		// effect, and hoisting it above `outerArg`/`capArgs` would reorder the emitted synthetic names.
		val ctorArgs: List<String> by lazy { filledArgs(node, outerArgLazy) }
		val arrArgs = if (arrElem != null) ctorArgs else emptyList()
		if (arrElem != null && arrArgs.size == 2)
			"""{"k":"newArrayInit","elem":${arrElem.toJson()},"size":${arrArgs[0]},"init":${arrArgs[1]}}"""
		else if (arrElem != null && arrArgs.size == 1)
			"""{"k":"newArraySized","elem":${arrElem.toJson()},"size":${arrArgs[0]}}"""
		else {
		// A generic .NET type (`Collection<Int>()`) -> a constructed `clrg:` spec; non-generic stays plain.
		val clr: TypeNode? = klass?.let { clrName(it) }?.let { net ->
			val args = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			if (args.isNullOrEmpty()) TypeNode.Fqn(net) else TypeNode.Fqn(net, args)
		}
		// A collection ctor `ArrayList<R>()` / `HashSet<T>()` (kotlin.collections.* = java.util.* typealiases) -> the
		// BCL collection (`new List<R>()` / `new HashSet<T>()`): birType already maps the type. Lets the real stdlib
		// `map`/`filter`/`mapTo` (which build an ArrayList) compile straight to the BCL collection DotKt uses.
		// A builtin-exception ctor (`throw IllegalStateException(msg)`) is NOT mapped here: it emits a plain `new
		// @kotlin.IllegalStateException` and bir2cir rewrites it to `newClr System.X` off the stdlib's @ClrTypeAlias.
		if (clr != null)
			"""{"k":"new","type":${clr.toJson()},"argTypes":[${node.symbol.owner.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(",") { birType(it.type).toJson() }}],"args":[${ctorArgs.joinToString(",")}]}"""
		else {
			val outerArg = outerArgLazy.value
			// A lifted local class prepends its captured outer locals (evaluated here, in the outer context).
			val capArgs = klass?.let { localClassCaptures[it] }?.map { capValueExpr(it) } ?: emptyList()
			val args = (listOfNotNull(outerArg) + capArgs + ctorArgs).joinToString(",")
			// The resolved ctor's regular-parameter STATIC TYPES, as pure Kotlin FQNs (bir2cir/ilemit derive the CLR
			// forms — kotc emits identity, not resolution). This lets a `new` of a type with overloaded constructors
			// resolve by SIGNATURE, not by arg count alone (mirrors the .NET-owner `new` branch above, which carries `argTypes`). Only the ctor's
			// OWN params are described — prepended enclosing/capture args are not — so a consumer uses these only when
			// their count lines up with the emitted args (in-assembly types stay arity-resolved).
			val ctorArgTypes = node.symbol.owner.parameters
				.filter { it.kind == IrParameterKind.Regular }
				.joinToString(",") { birType(it.type).toJson() }
			// `ownerSpec` names a lifted generic-capturing LOCAL CLASS as its CONSTRUCTED `L<T>` (own args from
			// `node.type` + the enclosing captured params it recorded in `liftedTypeArgParams`), so a
			// `fun <T> f(){ class L{ val x:T=t }; L() }` instantiates `L<T>` at each `new` site. A non-generic local
			// class / any other type keeps the plain identity.
			"""{"k":"new","type":${(klass?.let { ownerSpec(it, node.type) } ?: OBJ).toJson()},"argTypes":[$ctorArgTypes],"args":[$args]}"""
		}
		}
	}
	// A string template (`"$x"`). #59: emit ONLY the FAITHFUL concat. bir2cir (FaithfulHintRecognition) recovers each
	// part's static type via StaticType and wraps a collection/Map part in clrCollToString/clrMapToString (Kotlin-style
	// `[a, b]` / `{a=1, b=2}`, else `"$map"` yields the raw .NET type name) and a NULLABLE part in LibraryKt.toString
	// (null -> "null", else a null ref appends empty).
	is IrStringConcatenation -> """{"k":"concat","parts":[${node.arguments.joinToString(",") { expr(it) }}]}"""
	is IrTypeOperatorCall -> when (node.operator) {
		// `x is T` (exhaustive when matching) -> isinst + not-null check.
		IrTypeOperator.INSTANCEOF -> """{"k":"isInst","type":${birType(node.typeOperand).toJson()},"e":${expr(node.argument)}}"""
		IrTypeOperator.NOT_INSTANCEOF -> """{"k":"unaryOp","op":"!","e":{"k":"isInst","type":${birType(node.typeOperand).toJson()},"e":${expr(node.argument)}}}"""
		// `x as T` / smart-cast downcast -> castclass (or unbox for value types). Throws on mismatch.
		// A value-type-nullable source (`Int?` = `Nullable<T>`) cast to its non-null value (`Int`) must UNWRAP
		// `Nullable<T>.Value` — `unbox.any int` over a `Nullable<int>` struct is invalid IL / garbage (the C1
		// miscompile when FIR carries the smart-cast as an explicit IMPLICIT_CAST node instead of narrowing the
		// IrGetValue directly). The twin of the IrGetValue narrowing path above.
		IrTypeOperator.CAST, IrTypeOperator.IMPLICIT_CAST ->
			nullableValueUnwrapElem(node.argument.type, node.typeOperand)?.let { elem ->
				"""{"k":"nullableValue","elem":${elem.toJson()},"e":${expr(node.argument)}}"""
			} ?: """{"k":"cast","type":${birType(node.typeOperand).toJson()},"e":${expr(node.argument)}}"""
		// `x as? T` -> null on mismatch. Reference T: `isinst T` (null or ref). Value T: `T?` (Nullable<T>).
		IrTypeOperator.SAFE_CAST -> {
			// A value primitive OR an unsigned inline-class (`UInt`/…, #126) `T` -> the value-type nullable path
			// (`safeCastValue` = `Nullable<T>`): unsigned is a value type on the CLR, so `x as? UInt` must yield
			// `Nullable<uint>`, not a boxed reference via `isInstRef` (same #118 class as `!!`/smart-cast).
			val velem = node.typeOperand.takeIf { it.isPrimitiveOrUnsigned() }?.classFqName?.asString()?.let { TypeNode.Fqn(it) }
			if (velem != null) """{"k":"safeCastValue","elem":${velem.toJson()},"e":${expr(node.argument)}}"""
			else """{"k":"isInstRef","type":${birType(node.typeOperand).toJson()},"e":${expr(node.argument)}}"""
		}
		// A fun-interface SAM conversion (`Comparator { a, b -> … }`) -> a synthetic class implementing the interface
		// (the SAM method = the lambda body), NOT a Func delegate -- a delegate has no `compare` so a call site that
		// uses the value by interface (`comparator.compare(...)`) throws EntryPointNotFound. See samConversion.
		IrTypeOperator.SAM_CONVERSION -> samConversion(node)
		// Coercions to Unit / not-null pass the value through.
		else -> expr(node.argument)
	}
	is IrWhen -> ternary(node)
	// `try { … } catch { … }` in VALUE position (`val x = try …`, `return try …`, a try in a lambda) -> a temp
	// local assigned in each branch, wrapped in a valueBlock (a CLR try/catch leaves no value on the stack).
	is IrTry -> tryExpr(node)
	// `T::class` / `Foo::class` -> a System.Type token. For a generic param `T` this is `ldtoken !!0` in the
	// generic method (CLR reified generics); `Foo::class` is a concrete `ldtoken Foo`.
	is IrClassReference -> """{"k":"classRef","type":${birType(node.classType).toJson()}}"""
	// `x::class` (runtime class of an instance) -> `x.GetType()` (a System.Type); `.simpleName`/`.qualifiedName`
	// on the result route to Type.Name/FullName, same as the `T::class` literal path.
	is IrGetClass -> """{"k":"getType","e":${expr(node.argument)}}"""
	// `throw` in expression position (e.g. `x ?: throw ...`, `if (c) v else throw ...`): type Nothing,
	// transfers control so no value reaches the surrounding merge point.
	is IrThrow -> throwExpr(expr(node.value))
	// `return` used in expression position (`val x = if (c) a else return`; `x ?: return -1`). Like throwExpr,
	// it transfers control so no value reaches the surrounding merge.
	is IrReturn -> {
		// A `return` targeting a kotc-SPLICED inline fn/lambda (target in inlineReturnSubst) is a lambda-LOCAL return,
		// NOT a caller return: route it to the splice's result-local + end-label, wrapped as an expression-position
		// control transfer via breakContinueExpr — the SAME routing the statement-position arm does
		// (BirEmitterStatements, `spliced`). A raw `{"k":"returnExpr"}` here would leak into the inline lambda carrier,
		// indistinguishable from a genuine non-local return, and bir2cir's MaterializeCarrier rejects it fail-loud.
		val spliced = inlineReturnSubst[node.returnTargetSymbol]
		if (spliced != null) {
			val (res, end) = spliced
			val goto = """{"k":"goto","id":$end}"""
			// Unlike the NON-spliced arm below, the value stored into the splice result-local needs NO return-site
			// coerceValue/wrapReturnNonNull: a `return@lambda <value-type-nullable>` into a bare-value slot is only
			// well-typed via a smart-cast, which Fir2Ir always materializes as a narrowed IrGetValue or an IMPLICIT_CAST
			// — both already `nullableValue`-unwrapped by expr()'s leaf arms — so node.value is already the bare `Int`;
			// and a splice target is always a LAMBDA literal, never a postcondition-registered public fn. Verified a
			// pure no-op across the value-nullable/smart-cast/generic battery (cases/il-inlineretcoerce).
			val xfer = if (res != null) """{"k":"setLocal","name":${str(res)},"value":${expr(node.value)}},$goto"""
				else if (node.value is IrGetObjectValue) goto
				// Unit splice: evaluate a side-effecting return value for its effect, then jump.
				else """{"k":"exprStmt","expr":${expr(node.value)}},$goto"""
			breakContinueExpr(xfer)
		}
		// A genuine NON-LOCAL return stays a raw returnExpr (bir2cir routes it at splice time). A Unit-typed return
		// VALUE can still be a SIDE-EFFECTING call (`x ?: return unitFn()`): evaluate it, then transfer — a bare
		// `{"k":"returnExpr"}` (the old behavior) silently DROPPED the call. A plain Unit ref (IrGetObjectValue) has
		// nothing to evaluate. Mirrors the statement-position arm's Unit-return handling.
		else if (!node.value.type.isUnit()) {
			val retType = (node.returnTargetSymbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.returnType
			val v0 = if (retType != null) coerceValue(node.value, retType) else expr(node.value)
			// #6 non-null RETURN POSTCONDITION: expression-position returns need the same bind-check-throw as
			// statement-position returns. Skip Nothing values (`return TODO()` already throws) and inline splices,
			// which took the branch above.
			val postMsg = postconditionReturns[node.returnTargetSymbol]
			val v = if (postMsg != null && retType != null && !node.value.type.isNothing()) wrapReturnNonNull(v0, retType, postMsg) else v0
			"""{"k":"returnExpr","value":$v}"""
		}
		else if (node.value is IrGetObjectValue) """{"k":"returnExpr"}"""
		else breakContinueExpr("""{"k":"exprStmt","expr":${expr(node.value)}},{"k":"return"}""")
	}
	// `break`/`continue` used in expression position (`val end = if (c) x else break`, stdlib CharSequence.windowed's
	// coercedEnd). Kotlin types them `Nothing`: they transfer control, so no value reaches the surrounding merge. We
	// have no bare control-transfer EXPRESSION node (goto/break are statements), so emit the SAME control transfer as
	// stmt() inside a valueBlock, then an unreachable `throw null` result — after the goto/break jumps away the throw
	// is dead code, but it gives the valueBlock a well-formed result that never falls through to the cond merge point
	// (so the merge keeps only the live branch's type, exactly like a throwExpr/returnExpr branch). Reuses existing
	// ilemit nodes only (goto/break/throwExpr) — no new backend vocabulary.
	is IrBreak -> breakContinueExpr(cfgLoopStack.lastOrNull { it.first === node.loop }
		?.let { """{"k":"goto","id":${it.third}}""" } ?: """{"k":"break","label":${labelJson(node.label)}}""")
	is IrContinue -> breakContinueExpr(cfgLoopStack.lastOrNull { it.first === node.loop }
		?.let { """{"k":"goto","id":${it.second}}""" } ?: """{"k":"continue","label":${labelJson(node.label)}}""")
	is IrCall -> call(node)
	// A callable reference to a property (`::x`/`obj::p`/`Type::p`) -> a lifted class implementing the real
	// stdlib KProperty0/KMutableProperty0/KProperty1/KMutableProperty1 interface (#70); see `propertyRef`. The
	// compiler-synthesized KProperty argument of a delegate's getValue/setValue is a separate, cheaper path
	// (`kPropertyStub`, materialized directly at the delegate call sites — never reaching this dispatch).
	is IrPropertyReference -> propertyRef(node)
	is IrFunctionExpression -> lambda(node)
	// A callable reference `::foo` -> a delegate bound to the referenced function (same Func/Action as a lambda).
	is IrFunctionReference -> functionRef(node)
	// A `vararg` argument -> a newArray. A spread `*a` (IrSpreadElement) passes an existing array: a lone
	// spread is forwarded as-is; all-literal builds a fresh array; mixed `f(1,*a,2)` is a clean deferral.
	is IrVararg -> {
		val spreads = node.elements.filterIsInstance<IrSpreadElement>()
		val directs = node.elements.filterIsInstance<IrExpression>()
		when {
			spreads.size == 1 && directs.isEmpty() -> expr(spreads[0].expression)
			spreads.isEmpty() -> """{"k":"newArray","elem":${birType(node.varargElementType).toJson()},"elems":[${directs.joinToString(",") { expr(it) }}]}"""
			// `f(1, *a, 2)` -> build a List<elem> (Add literals / AddRange spreads), then ToArray.
			else -> {
				val parts = node.elements.joinToString(",") { e ->
					when (e) {
						is IrSpreadElement -> """{"spread":true,"e":${expr(e.expression)}}"""
						is IrExpression -> """{"spread":false,"e":${expr(e)}}"""
						else -> """{"spread":false,"e":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}}"""
					}
				}
				"""{"k":"spreadConcat","elem":${birType(node.varargElementType).toJson()},"parts":[$parts]}"""
			}
		}
	}
	else -> unsupported(node, "this expression", "the IR node ${node::class.simpleName} has no .NET lowering")
}
