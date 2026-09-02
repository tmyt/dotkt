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
	// A value the ENCLOSING call's evaluation plan bound (§2.7): every reader renders as that binding's `bindRef` read.
	planReads[node]?.let { return it }
	// Only a CALL can supply values that need a plan; everything else renders straight through.
	if (node !is org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression) {
		val rendered = exprInner(node)
		val withMember = if (node is IrGetField) memberFieldVisibilityStamped(node.symbol.owner, rendered) else rendered
		return styStamped(node, withMember)
	}
	val (plan, s) = withCallPlan(node) {
		val rendered = exprInner(node)
		// A LOCAL delegated access renders as the delegate member (there is no CLR property accessor), so the local
		// accessor's visibility is not this node's fact. Stamp the actual operator target when one exists.
		val inlined = delegateInlinedAccess?.takeIf { it.first === node }
		val withMember = if (inlined != null)
			inlined.second?.let { memberVisibilityStamped(it, rendered) } ?: rendered
		else memberVisibilityStamped(node, rendered)
		styStamped(node, withMember)
	}
	return plan.wrap(s, birType(node.type).toJson())
}

/**
 * A default/inline body can cross an assembly before bir2cir selects its physical CLR owner. Preserve the frontend
 * fact that a function access targets a private/protected declaration; the consuming bir2cir can then author a
 * caller-side UnsafeAccessor without rediscovering Kotlin lexical privilege from a CLR reference.
 */
private fun BirEmitter.memberVisibilityStamped(
	node: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression,
	s: String,
): String = memberVisibilityStamped(
	node.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility ?: return s,
	s,
	preserveDeclaration = node is IrCall && node.superQualifierSymbol != null,
)

/** The callable-reference path is not an [IrFunctionAccessExpression], but carries the same resolved declaration. */
internal fun BirEmitter.memberVisibilityStamped(
	target: org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility,
	s: String,
	preserveDeclaration: Boolean = false,
): String {
	val visibility = visOf(target)
	val restricted = visibility == "private" || visibility == "protected"
	if (!restricted && !preserveDeclaration) return s
	if (!(s.startsWith("{\"k\":\"callInstance\"") || s.startsWith("{\"k\":\"callStatic\"") ||
			s.startsWith("{\"k\":\"new\"") || s.startsWith("{\"k\":\"newBoundDelegate\""))) return s
	val ownerTypeParams = memberOwnerTypeParamsJson(target)
	val methodTypeParams = (target as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.let {
		typeParamsJson(it.typeParameters)
			.replaceFirst(",\"typeParams\":", ",\"memberMethodTypeParams\":")
	}.orEmpty()
	val declarationFact = when (target) {
		is org.jetbrains.kotlin.ir.declarations.IrConstructor -> {
			// Ordinary same-unit construction already carries the COMPLETE declaration vector, including synthetic
			// enclosing/capture slots, from the IrConstructorCall arm below.  Do not overwrite it with the source-only
			// regular vector when visibility stamping is also required.
			if (s.contains("\"memberSignature\"")) "" else {
			val signature = target.parameters
				.filter { it.kind == org.jetbrains.kotlin.ir.declarations.IrParameterKind.Regular }
				.joinToString(",") { birType(it.type).toJson() }
			",\"memberSignature\":[${signature}]"
			}
		}
		is org.jetbrains.kotlin.ir.declarations.IrFunction -> {
			val signature = overloadSigField(target)
				.replaceFirst(",\"sig\":", ",\"memberSignature\":")
			signature + ",\"memberReturnType\":" + birType(target.returnType).toJson()
		}
		else -> ""
	}
	val visibilityFact = if (restricted) ",\"memberVisibility\":" + str(visibility) else ""
	return s.dropLast(1) + visibilityFact + ownerTypeParams + methodTypeParams +
		declarationFact + "}"
}

/**
 * Declaration-form generic facts for a private/protected member's semantic owner. These stay in Kotlin vocabulary;
 * bir2cir decides whether the lexical edge needs a CLR UnsafeAccessor and, if so, maps this frame to method slots.
 */
private fun BirEmitter.memberOwnerTypeParamsJson(
	target: org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility,
): String {
	val owner = target.parent as? org.jetbrains.kotlin.ir.declarations.IrClass ?: return ""
	val params = innerEnclosingTypeParams(owner) + owner.typeParameters
	return typeParamsJson(params).replaceFirst(",\"typeParams\":", ",\"memberOwnerTypeParams\":")
}

/** Preserve the declaration type as well as visibility: a write value can be a subtype and is not a field signature. */
internal fun BirEmitter.memberFieldVisibilityStamped(
	field: org.jetbrains.kotlin.ir.declarations.IrField,
	s: String,
): String {
	val visibility = visOf(field)
	if (visibility != "private" && visibility != "protected") return s
	if (!(s.startsWith("{\"k\":\"field\"") || s.startsWith("{\"k\":\"lateinitGet\"") ||
			s.startsWith("{\"k\":\"staticField\"") || s.startsWith("{\"k\":\"setField\"") ||
			s.startsWith("{\"k\":\"setFieldExpr\"") || s.startsWith("{\"k\":\"staticFieldSet\""))) return s
	return s.dropLast(1) + ",\"memberVisibility\":" + str(visibility) + memberOwnerTypeParamsJson(field) +
		",\"memberType\":" + birType(field.type).toJson() + "}"
}

/** #122's `sty` stamp on the value-node kinds bir2cir's StaticType reads a type from (see the note above). */
private fun BirEmitter.styStamped(node: IrExpression, s: String): String =
	if (styNodePrefixes.any { s.startsWith(it) }) """{"sty":${birType(node.type).toJson()},${s.substring(1)}""" else s

/** The plan's STABILITY test: a const or a read of an immutable non-ref-cell local/parameter re-reads for free and
 *  without side effects, so a binding for it may be inlined at every reader and may move past another value without
 *  becoming observable. Judged ONCE here and recorded on the binding (`stable`); bir2cir consumes the answer rather
 *  than re-deriving it. */
internal fun BirEmitter.isStableValue(e: IrExpression): Boolean =
	e is IrConst || (e as? IrGetValue)?.symbol?.owner?.let { o ->
		!isRefCell(o) && (o is IrValueParameter || (o as? IrVariable)?.isVar == false)
	} == true

private val styNodePrefixes = listOf(
	"""{"k":"local"""", """{"k":"callStatic"""", """{"k":"callInstance"""",
	"""{"k":"field"""", """{"k":"lateinitGet"""", """{"k":"staticField"""",
)

/**
 * The compiler-authored leading parameter of a Kotlin inner constructor is the selected inner class's immediate
 * enclosing owner. The value supplied for that slot may have a derived static type, but that value type is not the
 * constructor declaration type. The constructed inner type already carries Kotlin's selected semantic application as
 * [own..., enclosing...]; derive the immediate outer application from that same fact bir2cir uses to close the inner
 * owner, rather than independently re-walking the receiver's supertype graph.
 */
private fun BirEmitter.innerConstructorOuterSlotJson(
	node: IrConstructorCall,
	innerClass: IrClass?,
): String? {
	if (innerClass?.isInner != true) return null
	val outerClass = innerClass.parent as? IrClass
		?: return invariantBroken(node, "an inner constructor's class has no enclosing class")
	if (dispatchReceiver(node) == null)
		return invariantBroken(node, "an inner constructor call has no enclosing-instance receiver")
	val innerApplication = ownerSpec(innerClass, node.type)
	val arguments = (innerApplication as? TypeNode.Fqn)?.args.orEmpty()
	val ownCount = innerClass.typeParameters.size
	val outerCount = innerSemanticEnclosingTypeParams(innerClass).size
	if (arguments.size != ownCount + outerCount)
		return invariantBroken(node,
			"an inner constructor result does not carry its complete own and enclosing type application")
	val outerArguments = arguments.drop(ownCount)
	val outerName = clrName(outerClass) ?: typeName(outerClass)
	return if (outerArguments.isEmpty()) TypeNode.Fqn(outerName).toJson()
	else TypeNode.Fqn(outerName, outerArguments).toJson()
}

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
			// The enum-entry NAME is Kotlin declaration identity. Its ordinal remains the complete physical value for a
			// locally-declared Kotlin enum; bir2cir uses the name plus referenced-DLL metadata to resolve a sparse CLR
			// enum's actual underlying constant.
			"""{"k":"enumValue","type":${fqnJson(parent?.let { typeName(it) } ?: "kotlin.Any")},"entry":${str(entry.name.asString())},"ordinal":$ord}"""
		}
	}
	// `object Foo` reference -> load the singleton `Foo.INSTANCE` static field (item 10). (Projected .NET objects
	// like Math are static call sites handled at the call site; only user singletons reach here as a value.)
	// The `Unit` object as a VALUE (e.g. `Result.success(Unit)`) is just another singleton: the stdlib's own
	// `kotlin.Unit` object INSTANCE (this-assembly under stdlib-compile, else resolved against the referenced
	// stdlib) — no DotKt.Runtime. A dll2klib declaration whose Kotlin object identity differs from its physical CLR
	// singleton forwards its existing @ClrExternal owner fact here, matching the external IrGetField path below and
	// external call ownerType nodes. This does not infer a CLR name or shape; bir2cir still binds the carried owner.
	is IrGetObjectValue -> if (node.symbol.owner.isCompanion) {
		val companion = node.symbol.owner
		val owner = companion.parent as IrClass
		"""{"k":"companionValue","ownerType":${fqnJson(typeName(owner))},"companionType":${fqnJson(typeName(companion))},"name":${str(companion.name.asString())}}"""
	} else
		"""{"k":"staticField","ownerType":${fqnJson(clrExternalOwner(node.symbol.owner) ?: typeName(node.symbol.owner))},"name":"INSTANCE"}"""
	is IrBlock -> blockExpr(node)
	is IrGetField -> {
		val staticOwner = staticBackingFieldOwner(node.symbol.owner)
		val ownerClass = node.symbol.owner.parent as? IrClass
		val clr = ownerClass?.let { clrName(it) }
		val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
		val fldName = node.symbol.owner.name.asString()
		val copyDefault = if (activeDataClassCopyDefault) ",\"dataClassCopyDefault\":true" else ""
		val dataClassEquals = if (activeDataClassEqualsFieldRead) ",\"dataClassEqualsFieldRead\":true" else ""
		// #89: a STATIC backing field (top-level property -> file class; companion property -> enclosing class) ->
		// a `staticField` load with NO receiver. Reached from the property's OWN custom accessor body reading
		// `field`; a plain field-only property is read directly at the call site (BirEmitterCalls).
		if (staticOwner != null) {
			val lateinit = node.symbol.owner.correspondingPropertySymbol?.owner?.let { isLateinitProperty(it) } == true
			if (lateinit)
				"""{"k":"lateinitGet","ownerType":${fqnJson(staticOwner)},"static":true,"name":${str(fldName)}}"""
			else """{"k":"staticField","ownerType":${fqnJson(staticOwner)},"name":${str(fldName)}}"""
		}
		// `Throwable.message`/`.cause` are PLAIN Kotlin properties: an app read is an IrCall(get_message) routed by
		// bir2cir to clrPropGet System.Exception.Message off the @ClrProperty binding (layer purity — no BCL member name
		// in kotc). A direct backing-FIELD read reaching here is only kotlin.Throwable's own generated getter body in the
		// stdlib ref build, where `message` is a real field — the plain `field` path below serves it.
		else if (clr != null)
			"""{"k":"field","ownerType":${fqnJson(clr)},"recv":$recvJson,"name":${str(fldName)}}"""
		// A `lateinit var` backing-field read -> throw if still uninitialized (null) — proper lateinit semantics.
		else if (node.symbol.owner.correspondingPropertySymbol?.owner?.let { isLateinitProperty(it) } == true)
			"""{"k":"lateinitGet","ownerType":${ownerSpec(ownerClass, node.receiver?.type).toJson()},"recv":$recvJson,"name":${str(fldName)}}"""
		else
			"""{"k":"field","ownerType":${ownerSpec(ownerClass, node.receiver?.type).toJson()},"recv":$recvJson,"name":${str(fldName)}$copyDefault$dataClassEquals}"""
	}
	is IrConstructorCall -> {
		val klass = node.symbol.owner.parent as? IrClass
		val innerOuterSlot = innerConstructorOuterSlotJson(node, klass)
		// The generic Kotlin array constructor `Array<E>(size) { init }` is semantic BIR array construction. Carry the
		// frontend element type exactly as supplied, whether concrete or a scoped `tv`; bir2cir owns its physical CLR
		// representation. Without this node, the call falls through to a bogus `new kotlin.Array(...)` construction.
		// The SIGNED primitive array ctor (`IntArray(size){init}`) is NOT decomposed here: kotc emits the faithful
		// `new kotlin.IntArray(size, init)` ctor call (the normal-new fall-through below) and bir2cir DERIVES the
		// newArrayInit/newArraySized construction off the faithful `kotlin.IntArray` identity + its element.
		val arrElem: TypeNode? =
			if (klass?.fqNameWhenAvailable?.asString() == "kotlin.Array") {
				val elemType = (((node.type as? IrSimpleType)?.arguments?.firstOrNull()) as? IrTypeProjection)?.type
				elemType?.let(::birType)
			} else null
		// The ctor's regular args, omitted defaults filled — the SAME single pass every call shape uses. Emitted ONCE
		// (re-running it would duplicate any lift/lambda emission side effect), and BEFORE the enclosing-instance
		// argument below. An inner-class ctor takes that instance — its own DISPATCH receiver — as a leading arg, so when
		// this call needs an evaluation plan `filledArgs` binds it as a recv-phase binding and the reader below renders
		// that ONE binding's `bindRef` through the ordinary `expr()`, instead of a second emission of the expression
		// (which would append a second copy of any lifted lambda in it).
		val ctorArgs: List<String> = filledArgs(node)
		val outerArg: String? =
			if ((node.symbol.owner.parent as? IrClass)?.isInner == true) dispatchReceiver(node)?.let { expr(it) } else null
		val arrArgs = if (arrElem != null) ctorArgs else emptyList()
		if (arrElem != null && arrArgs.size == 2)
			"""{"k":"newArrayInit","elem":${arrElem.toJson()},"size":${arrArgs[0]},"init":${arrArgs[1]}}"""
		else if (arrElem != null && arrArgs.size == 1)
			"""{"k":"newArraySized","elem":${arrElem.toJson()},"size":${arrArgs[0]}}"""
		else {
		// A generic .NET type (`Collection<Int>()`) -> a constructed `clrg:` spec; non-generic stays plain.
		val clr: TypeNode? = klass?.let { clrName(it) }?.let { net ->
			val args = (node.type as? IrSimpleType)?.arguments
				?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			if (args.isNullOrEmpty()) TypeNode.Fqn(net) else TypeNode.Fqn(net, args)
		}
		// A collection ctor `ArrayList<R>()` / `HashSet<T>()` (kotlin.collections.* = java.util.* typealiases) -> the
		// BCL collection (`new List<R>()` / `new HashSet<T>()`): birType already maps the type. Lets the real stdlib
		// `map`/`filter`/`mapTo` (which build an ArrayList) compile straight to the BCL collection DotKt uses.
		// A builtin-exception ctor (`throw IllegalStateException(msg)`) is NOT mapped here: it emits a plain `new
		// @kotlin.IllegalStateException` and bir2cir rewrites it to `newClr System.X` off the stdlib's @ClrTypeAlias.
		if (clr != null) {
			// A referenced DotKt inner class carries a physical @ClrExternal owner, but it is still a Kotlin inner
			// construction: the enclosing instance is a leading constructor argument in the CLR representation just as
			// it is for a same-module declaration. A foreign CLR nested type is never `isInner`, so it keeps its ordinary
			// declared constructor vector.
			val externalArgs = (listOfNotNull(outerArg) + ctorArgs).joinToString(",")
			val ctorSubst = callSiteSubstitutor(node, node.symbol.owner)
			val externalTypes = (listOfNotNull(innerOuterSlot) + node.symbol.owner.parameters
				.filter { it.kind == IrParameterKind.Regular }
				.map { birType(ctorSubst?.substitute(it.type) ?: it.type).toJson() }).joinToString(",")
			"""{"k":"new","type":${clr.toJson()},"argTypes":[$externalTypes],"args":[$externalArgs]}"""
		}
		else {
			// A lifted local class prepends its captured outer locals (evaluated here, in the outer context).
			val capArgs = klass?.let { localClassCaptures[it] }?.map { capValueExpr(it) } ?: emptyList()
			val args = (listOfNotNull(outerArg) + capArgs + ctorArgs).joinToString(",")
			// Carry the complete selected declaration shape, including kotc-authored enclosing/capture slots. These entries
			// correspond index-for-index with `args`; bir2cir links the exact local constructor and never chooses by arity.
			val capTypes = klass?.let { localClassCaptures[it] }.orEmpty()
				.map { str(captureFieldType(it)) }
			// Constructor parameter declarations live in the constructed class's generic frame. Close that frame at
			// this call site before publishing the selected signature: for `class L<U>(u: U); L(0)` the argument slot
			// is `Int`, not whichever type variable happens to occupy index zero in the enclosing caller. This is the
			// same callee-to-caller substitution used by default-argument rendering.
			val ctorSubst = callSiteSubstitutor(node, node.symbol.owner)
			val regularTypes = node.symbol.owner.parameters.filter { it.kind == IrParameterKind.Regular }
				.map { birType(ctorSubst?.substitute(it.type) ?: it.type).toJson() }
			val ctorArgTypes = (listOfNotNull(innerOuterSlot) + capTypes + regularTypes).joinToString(",")
			// Preserve the frontend-selected OPEN regular-parameter declaration independently from the substituted use-site
			// vector. The compiler-authored outer slot has no IrValueParameter, so its exact declaration application is the
			// projected enclosing owner above. Include every leading slot index-for-index; this lets bir2cir bind an inner or
			// capturing constructor without reconstructing them, and lets Root-V later close `X` to the invariant physical
			// owner argument even when the supplied value retains its read-only head view.
			val regularMemberTypes = node.symbol.owner.parameters.filter { it.kind == IrParameterKind.Regular }
				.map { birType(it.type).toJson() }
			val ctorMemberSignature = (listOfNotNull(innerOuterSlot) + capTypes + regularMemberTypes).joinToString(",")
			// `ownerSpec` names a lifted generic-capturing LOCAL CLASS as its CONSTRUCTED `L<T>` (own args from
			// `node.type` + the enclosing captured params it recorded in `liftedTypeArgParams`), so a
			// `fun <T> f(){ class L{ val x:T=t }; L() }` instantiates `L<T>` at each `new` site. A non-generic local
			// class / any other type keeps the plain identity.
			"""{"k":"new","type":${(klass?.let { ownerSpec(it, node.type) } ?: OBJ).toJson()},"argTypes":[$ctorArgTypes],"args":[$args],"memberSignature":[$ctorMemberSignature]}"""
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
		val retType = (node.returnTargetSymbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.returnType
		// Unit is elided only for a Unit-returning TARGET. The value's own type is not enough: `return@lambda Unit`
		// from an `Any?`-returning lambda is a real Unit value and must survive into BIR.
		val targetOmitsValue = retType?.isUnit() ?: node.value.type.isUnit()
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
		// A genuine NON-LOCAL return stays a raw returnExpr (bir2cir routes it at splice time). For a Unit-returning
		// TARGET, its value can still be a SIDE-EFFECTING call (`x ?: return unitFn()`): evaluate it, then transfer —
		// a bodyless `{"k":"returnExpr"}` would silently drop the call. A plain Unit ref (IrGetObjectValue) has
		// nothing to evaluate. Mirrors the statement-position arm's Unit-return handling.
		else if (!targetOmitsValue) {
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
