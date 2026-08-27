package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.fir.backend.FirMetadataSource
import org.jetbrains.kotlin.fir.containingClassLookupTag
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrDeclaration
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrField
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.fir.declarations.utils.isLateInit
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrDelegatingConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrClassReference
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrEnumConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrExpressionBody
import org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression
import org.jetbrains.kotlin.ir.declarations.IrEnumEntry
import org.jetbrains.kotlin.ir.expressions.IrGetEnumValue
import org.jetbrains.kotlin.ir.expressions.IrGetField
import org.jetbrains.kotlin.ir.expressions.IrGetObjectValue
import org.jetbrains.kotlin.ir.expressions.IrInstanceInitializerCall
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
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.expressions.IrGetClass
import org.jetbrains.kotlin.ir.declarations.isStaticMethodOfClass
import org.jetbrains.kotlin.ir.declarations.IrLocalDelegatedProperty
import org.jetbrains.kotlin.ir.declarations.IrValueDeclaration
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.util.classId
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.util.resolveFakeOverride
import org.jetbrains.kotlin.ir.util.primaryConstructor
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
import org.jetbrains.kotlin.ir.interpreter.IrInterpreter
import org.jetbrains.kotlin.ir.interpreter.checker.EvaluationMode
import org.jetbrains.kotlin.ir.interpreter.checker.IrInterpreterCheckerData
import org.jetbrains.kotlin.ir.interpreter.checker.IrInterpreterCommonChecker
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

/**
 * Kotlin 2.4 `class C { companion { … } }` (LanguageFeature.CompanionBlocksAndExtensions): a member written in
 * source INSIDE a class body that the frontend resolved as a STATIC member of that class.
 *
 * The fact is the frontend's, not ours to re-derive: FIR sets `status.isStatic` for a companion-block member and
 * therefore builds it with NO dispatch receiver type, and fir2ir projects exactly that by omitting the
 * dispatch-receiver parameter. So [isStaticMethodOfClass] — "member of an IrClass with no dispatch receiver" — IS
 * the projected `isStatic`, read here and carried into BIR unchanged. kotc decides no CLR placement from it.
 *
 * `origin == DEFINED` keeps frontend-SYNTHESIZED receiverless class members out of this partition (a rich enum's
 * `values`/`valueOf`, a callable-reference adapter): those have their own emission paths and their own owners.
 */
internal fun isKotlinStaticFunction(fn: IrSimpleFunction): Boolean =
	fn.origin == IrDeclarationOrigin.DEFINED && fn.isStaticMethodOfClass

/**
 * The property counterpart of [isKotlinStaticFunction]. FIR marks the whole property static, so fir2ir makes its
 * backing field `IrField.isStatic` AND its accessors receiverless; either alone is conclusive, and a computed
 * `companion val p get() = …` has only the latter.
 */
internal fun isKotlinStaticProperty(p: IrProperty): Boolean =
	p.origin == IrDeclarationOrigin.DEFINED && p.parent is IrClass &&
		(p.backingField?.isStatic == true ||
			p.getter?.let { it.isStaticMethodOfClass } == true ||
			p.setter?.let { it.isStaticMethodOfClass } == true)

/** Storage that is static in BIR while retaining its Kotlin declaration kind. A companion-block property carries
 * the frontend's explicit static fact. A `const val` declared by an object/companion object is likewise declaration
 * metadata rather than per-singleton state: CLR Literal requires static storage, and dll2klib needs its Constant row
 * to restore the const initializer for another Kotlin module. Only the former gets `kotlinStatic`; the latter remains
 * a member of the ordinary companion/object carrier. */
internal fun hasStaticPropertyStorage(klass: IrClass, p: IrProperty): Boolean =
	isKotlinStaticProperty(p) || (klass.kind == ClassKind.OBJECT && p.isConst)

/**
 * The fields backing a classifier's `companion { }` properties and an object's `const val` declarations. Storage
 * belongs to the TYPE, so the initializer travels WITH the field declaration: it must not run per-instance, and the
 * emitting layer runs a non-literal static field's `init` in the type initializer.
 *
 * Visibility and read-only follow the same rules as an instance backing field: an accessor-routed property keeps
 * its storage private behind accessors, while a non-routed one (`const`, `lateinit`, `@ClrField`) keeps the source
 * visibility and is marked read-only when it is not publicly settable.
 */
internal fun BirEmitter.staticPropertyFields(klass: IrClass): List<String> =
	klass.declarations.filterIsInstance<IrProperty>()
		.filter { hasStaticPropertyStorage(klass, it) }
		.mapNotNull { p ->
			val bf = p.backingField ?: return@mapNotNull null
			val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
			val routed = p.getter != null && !p.isConst && !p.isLateinit && !isClrField(p)
			val v = if (routed) "private" else visOf(p)
			val visJson = if (v != "public") ""","vis":${str(v)}""" else ""
			val ro = if (!routed && (!p.isVar || (p.setter != null && visOf(p.setter!!) != "public"))) ""","readOnly":true""" else ""
			val const = if (p.isConst) ""","const":true""" else ""
			val kotlinStatic = if (isKotlinStaticProperty(p)) ""","kotlinStatic":true""" else ""
			"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()},"static":true$kotlinStatic,"init":$init$visJson$ro$const${lateinitFieldFlag(p)}${volatileFieldFlag(p)}}"""
		}

/**
 * A STATIC member of a BASE type, materialized onto this class by the frontend — `class R : System.Random()` grows
 * an `R.Shared` for `Random.Shared`, so the name resolves through the subclass.
 *
 * There is nothing to emit for it. The CLR does not inherit statics into a derived TypeDef (`R.Shared` IS
 * `Random.Shared`, and a use site already names the RESOLVED declaring class), and a static occupies no virtual
 * slot, so re-declaring it here would produce a second, unrelated member that is simultaneously static and an
 * override.
 *
 * The discriminator is the OVERRIDE LINK, not the `isFakeOverride` convenience flag — which Kotlin 2.4 does not
 * set consistently after fir2ir — nor the declaration origin, which the same materialization leaves as `DEFINED`.
 * A static cannot override anything, so a static member that carries an override link is by construction not this
 * class's own declaration; a `companion { }` member never carries one. An ACCESSOR's own link may be empty while
 * its property's is not (the same asymmetry [overridesJson] walks the property chain for), so the property's link
 * counts too.
 */
internal fun isInheritedStaticFunction(fn: IrSimpleFunction): Boolean =
	fn.isStaticMethodOfClass &&
		(fn.overriddenSymbols.isNotEmpty() ||
			fn.correspondingPropertySymbol?.owner?.overriddenSymbols?.isNotEmpty() == true)

/** The property counterpart of [isInheritedStaticFunction]; either accessor's override link is conclusive. */
internal fun isInheritedStaticProperty(p: IrProperty): Boolean =
	p.getter?.let { isInheritedStaticFunction(it) } == true || p.setter?.let { isInheritedStaticFunction(it) } == true

/** #397: pure Kotlin accessor identity. The physical CLR method name is allocated by bir2cir. */
private fun BirEmitter.propertyAccessorFact(property: IrProperty, kind: String): String =
	""","propertyName":${str(property.name.asString())},"propertyAccessor":${str(kind)},"propertyAssociation":${str(propertyAssociation(property))}"""

/** #397: accessor-presence facts for a BIR property record; bir2cir authors its physical get/set links. */
private fun BirEmitter.kotlinPropertyAccessors(property: IrProperty, hasSetter: Boolean): String {
	val roles = if (hasSetter) "[\"get\",\"set\"]" else "[\"get\"]"
	return ""","kotlinAccessors":$roles,"propertyAssociation":${str(propertyAssociation(property))}"""
}

/** Whether FIR2IR explicitly materialized [fn] as an inherited declaration rather than a source declaration. */
private fun isInheritedSynthetic(fn: IrSimpleFunction): Boolean {
	// Delegation is a frontend-authored concrete forwarding declaration, never an inherited implementation. Its FIR
	// symbol is derived from the overridden member and can therefore retain supertype provenance even though the IR
	// declaration owns a real body in this class.
	if (fn.origin == IrDeclarationOrigin.DELEGATED_MEMBER) return false
	if (fn.isFakeOverride || fn.origin == IrDeclarationOrigin.FAKE_OVERRIDE) return true
	// Kotlin 2.4 can lose the IR convenience flag on a materialized declaration, notably across a reference KLIB.
	// FIR still owns and retains the semantic answer: substitution/intersection overrides say they came from supertypes,
	// while a "fake fake override" backed by the original declaration has a FIR owner different from its materialized IR
	// owner. This also keeps a real body-generating declaration such as DELEGATED_MEMBER out of the inherited-default
	// path; source offsets, bodies, and physical names are deliberately irrelevant.
	val fir = (fn.metadata as? FirMetadataSource.Function)?.fir as? FirCallableDeclaration
	if (fir?.origin == FirDeclarationOrigin.Delegated) return false
	if (fir?.origin?.fromSupertypes == true) return true
	val firOwner = fir?.containingClassLookupTag() ?: return false
	val irOwnerFir = ((fn.parent as? IrClass)?.metadata as? FirMetadataSource.Class)?.fir ?: return false
	return firOwner != irOwnerFir.symbol.toLookupTag()
}

/**
 * The concrete Kotlin declaration selected by frontend override resolution for an inherited member. This is a
 * frontend semantic fact: no CLR name, slot, or layout is projected here. A null result means that the inherited
 * member is still abstract.
 */
private fun selectedInheritedImplementation(fn: IrSimpleFunction): IrSimpleFunction? {
	if (!isInheritedSynthetic(fn)) return null
	return fn.resolveFakeOverride(::isInheritedSynthetic)?.takeIf { it.modality != Modality.ABSTRACT }
}

/** The resolved fake-override target may be a substituted IR copy whose callable and parent no longer carry imported
 *  annotations. The original declaration remains in the frontend override closure: select only declarations with the
 *  same Kotlin owner/member identity as the already-selected target, then transport their unique exact CLR owner. */
private fun BirEmitter.inheritedImplementationOwner(
	source: IrSimpleFunction,
	target: IrSimpleFunction,
	targetOwner: IrClass
): String? {
	val targetSemanticOwner = targetOwner.fqNameWhenAvailable?.asString()
	val targetProperty = target.correspondingPropertySymbol?.owner
	val targetMember = targetProperty?.name?.asString() ?: target.name.asString()
	val targetKind = when {
		targetProperty == null -> "method"
		target === targetProperty.getter -> "getter"
		else -> "setter"
	}
	val exactOwners = linkedSetOf<String>()
	val visitedFunctions = hashSetOf<IrSimpleFunction>()
	fun consider(candidate: IrSimpleFunction) {
		if (!visitedFunctions.add(candidate)) return
		val candidateOwner = candidate.parent as? IrClass
		val candidateProperty = candidate.correspondingPropertySymbol?.owner
		val candidateMember = candidateProperty?.name?.asString() ?: candidate.name.asString()
		val candidateKind = when {
			candidateProperty == null -> "method"
			candidate === candidateProperty.getter -> "getter"
			else -> "setter"
		}
		if (candidateOwner?.fqNameWhenAvailable?.asString() == targetSemanticOwner
			&& candidateMember == targetMember && candidateKind == targetKind) {
			val projectedOwner = candidateOwner?.let { overrideOwnerType(source, it) as? TypeNode.Fqn }?.name
			(projectedOwner ?: clrExternalOwner(candidate) ?: candidateOwner?.let(::clrExternalOwner))
				?.let(exactOwners::add)
		}
		candidate.overriddenSymbols.forEach { consider(it.owner) }
	}
	consider(source)
	val sourceProperty = source.correspondingPropertySymbol?.owner
	if (sourceProperty != null) {
		val visitedProperties = hashSetOf<IrProperty>()
		fun considerProperty(property: IrProperty) {
			if (!visitedProperties.add(property)) return
			(if (targetKind == "getter") property.getter else property.setter)?.let(::consider)
			property.overriddenSymbols.forEach { considerProperty(it.owner) }
		}
		considerProperty(sourceProperty)
	}
	if (exactOwners.size > 1)
		error("selected inherited implementation '$targetSemanticOwner.$targetMember' has multiple exact CLR owners: ${exactOwners.joinToString()}")
	return exactOwners.singleOrNull()
		?: clrExternalOwner(target)
		?: clrExternalOwner(targetOwner)
		?: targetSemanticOwner
}

/** Preserve the frontend-selected implementation identity instead of rediscovering it from ancestor bodies. */
private fun BirEmitter.inheritedImplementationFact(fn: IrSimpleFunction): String {
	val target = selectedInheritedImplementation(fn) ?: return ""
	val targetOwner = target.parent as? IrClass ?: return ""
	val owner = inheritedImplementationOwner(fn, target, targetOwner) ?: return ""
	val property = target.correspondingPropertySymbol?.owner
	val member = property?.name?.asString() ?: target.name.asString()
	val kind = when {
		property == null -> "method"
		target === property.getter -> "getter"
		else -> "setter"
	}
	return ""","inheritedImplementation":{"owner":${fqnJson(owner)},"member":${str(member)},"kind":${str(kind)},"arity":${target.typeParameters.size},"typeParams":${typeParamDeclarationsJson(target.typeParameters)}}"""
}

/**
 * A class emits no declaration for an accessor inherited from a default interface member. Keep the selected accessor
 * and its class-frame signature as a type-level BIR fact so bir2cir can decide whether CLR representation needs a
 * MethodImpl bridge, without re-resolving Kotlin override semantics.
 */
private fun BirEmitter.inheritedDefaultAccessorFact(property: IrProperty, accessor: IrSimpleFunction?, kind: String): String? {
	if (accessor == null || !isInheritedSynthetic(accessor)) return null
	val target = selectedInheritedImplementation(accessor) ?: return null
	val targetOwner = target.parent as? IrClass ?: return null
	if (targetOwner.kind != ClassKind.INTERFACE) return null
	val targetProperty = target.correspondingPropertySymbol?.owner ?: return null
	val targetKind = if (target === targetProperty.getter) "getter" else "setter"
	val targetOwnerName = inheritedImplementationOwner(accessor, target, targetOwner) ?: return null
	val extRecv = extensionReceiverParam(accessor)
	val parameterTypes = (listOfNotNull(extRecv?.type) + accessor.parameters.filter { isValueParameter(it) }.map { it.type })
		.joinToString(",") { birType(it).toJson() }
	val ret = if (kind == "get") birType(accessor.returnType) else TypeNode.Fqn("kotlin.Unit")
	return """{"propertyName":${str(property.name.asString())},"propertyAccessor":${str(kind)},"params":[$parameterTypes],"ret":${ret.toJson()},"implementation":{"owner":${fqnJson(targetOwnerName)},"member":${str(targetProperty.name.asString())},"kind":${str(targetKind)},"arity":${target.typeParameters.size},"typeParams":${typeParamDeclarationsJson(target.typeParameters)}}}"""
}

/** Ordinary-function twin of [inheritedDefaultAccessorFact]. */
private fun BirEmitter.inheritedDefaultMethodFact(fn: IrSimpleFunction): String? {
	if (fn.correspondingPropertySymbol != null || !isInheritedSynthetic(fn)) return null
	val target = selectedInheritedImplementation(fn) ?: return null
	val targetOwner = target.parent as? IrClass ?: return null
	if (targetOwner.kind != ClassKind.INTERFACE) return null
	val targetOwnerName = inheritedImplementationOwner(fn, target, targetOwner) ?: return null
	val extRecv = extensionReceiverParam(fn)
	val parameterTypes = (listOfNotNull(extRecv?.type) + fn.parameters.filter { isValueParameter(it) }.map { it.type })
		.joinToString(",") { birType(it).toJson() }
	return """{"member":${str(fn.name.asString())},"params":[$parameterTypes],"ret":${birType(fn.returnType).toJson()},"implementation":{"owner":${fqnJson(targetOwnerName)},"member":${str(target.name.asString())},"kind":"method","arity":${target.typeParameters.size},"typeParams":${typeParamDeclarationsJson(target.typeParameters)}}}"""
}

private fun BirEmitter.kotlinCompanionFact(owner: IrClass, companion: IrClass): String {
	val ownerName = owner.fqNameWhenAvailable?.asString()
		?: error("companion owner '${owner.name}' has no Kotlin qualified name")
	return ""","kotlinCompanion":{"owner":${str(ownerName)},"name":${str(companion.name.asString())},"visibility":${str(visOf(companion))}}"""
}

/** Kotlin interface supertypes as representation-neutral BIR identities. The declaration kind using this list is
 * irrelevant: interfaces, ordinary classes, and rich-enum classes must preserve the same frontend supertype facts. */
private fun BirEmitter.interfaceSuperTypes(klass: IrClass): String = klass.superTypes
	.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
	.mapNotNull { st ->
		// A stdlib/projected interface may already have a CLR-bound semantic type node; a user generic interface keeps
		// its Kotlin owner plus the actual constructed arguments. Function types are value representations, not an
		// implemented interface edge authored by this declaration. The Kotlin iterator/iterable protocol is deliberately
		// not special-cased: its real generic interface edge lets bir2cir's general reverse bridge synthesize
		// GetEnumerator from iterator().
		val bt = birType(st)
		val stClass = st.classifierOrNull?.owner as? IrClass
		when {
			bt is TypeNode.Fn -> null
			stClass != null && isExternalNetType(stClass) -> bt.toJson()
			else -> stClass?.let { ownerSpec(it, st).toJson() }
		}
	}
	.joinToString(",")

internal fun BirEmitter.interfaceDef(iface: IrClass): String {
	rejectClrEnumOnNonEnum(iface)
	fun ifaceMethod(fn: IrSimpleFunction, prop: IrProperty? = fn.correspondingPropertySymbol?.owner): String {
		val savedSemanticOwner = activeSemanticOwner
		activeSemanticOwner = semanticOwnerName(fn)
		// C3b reverse direction: a Kotlin interface extending a @Clr interface (Set : Collection->IReadOnlyCollection).
		// kotc emits the source property identity plus accessor role for both ref and runtime builds. bir2cir owns both
		// local physical allocation and any external CLR override-slot binding.
		val name = prop?.name?.asString() ?: fn.name.asString()
		val isSetter = prop != null && fn == prop.setter
		val accessorFact = prop?.let { propertyAccessorFact(it, if (isSetter) "set" else "get") } ?: ""
		val ret = if (isSetter) TypeNode.Fqn("kotlin.Unit") else birType(fn.returnType)
		// Return nullability (`fun <E> get(key): E?`) now rides the `ret` type node itself (`{t:nullable,of:tv}` from
		// the uniform birType) — the decl-level `retNullable` flag is RETIRED. bir2cir derives the nullable-generic
		// erasure from the type node, keeping the abstract slot symmetric with its concrete override.
		// A Kotlin interface method with a DEFAULT implementation (a body, not abstract) -> carry that body so ilemit
		// emits a CLR default interface method; an implementer that doesn't override it then INHERITS the default
		// instead of failing to load ("does not have an implementation", e.g. CoroutineContext.plus, ClosedRange.contains).
		val hasDefault = fn.body != null && fn.modality != Modality.ABSTRACT
		// A MEMBER-extension receiver is part of the Kotlin function signature. The class-method path already carries it
		// as the leading `__self` parameter; the interface-only path must be identical. Dropping it here made the abstract
		// slot `f(value)` while every implementation correctly emitted `f(__self,value)`, so otherwise-valid implementers
		// failed CLR type loading. This is still pure Kotlin vocabulary/fact preservation: no CLR owner/name is inferred.
		val extRecv = extensionReceiverParam(fn)
		if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
		// #6 non-null parameter PRECONDITIONS + return POSTCONDITION for a default interface method body (an abstract slot
		// has no body to guard).
		val body = if (hasDefault) {
			val stmts = withReturnPostcondition(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
			(preconditionChecks(fn) + listOfNotNull(stmts.takeIf { it.isNotEmpty() })).joinToString(",")
		} else ""
		activeSemanticOwner = savedSemanticOwner
		if (extRecv != null) selfSubst.remove(extRecv)
		val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
		val params = (listOfNotNull(selfParam) + paramsJsonList(fn.parameters, ownerFn = fn)).joinToString(",")
		// A generic interface method (`fun <E> get(...)`, `<R> fold(...)`) must carry its own type params, else
		// `gp:E`/`gp:R` in its signature is unresolvable at emit (CoroutineContext / ContinuationInterceptor / …).
		// `attrs`: ride the @Clr/[Kotlin*] metadata so the ref assembly carries the BCL binding hint (for app-emit
		// substitution). For a PROPERTY accessor the binding is on the property (size @ClrIntrinsic("Count")), so read from there.
		val memberAttrs = attrsJson((prop ?: fn).annotations)
		// A `suspend fun` interface member carries the SAME neutral `"suspend":true`+`resultType` FACT the concrete
		// `method()` path emits (BirEmitter.kt:1413). Without it bir2cir has nothing to key off for an INTERFACE
		// suspend member — it can't synthesize the Task-bridge signature / cold-entry — so a cross-assembly
		// `interface Fetcher { suspend fun fetch(): Int }` round-trip breaks (the abstract-CLASS path already tags it).
		// Preserve whether this declaration is a Kotlin IR fake override.  It is a Kotlin-side fact, not a CLR
		// decision: bir2cir uses it to distinguish a genuinely declared abstract override from an inherited member
		// that FIR/IR materialized on the derived interface.  Previously both shapes became identical empty methods,
		// forcing ilemit to rediscover the hierarchy and synthesize DIM forwarders.
		// The frontend's fake-override flag/origin is authoritative. A real declaration restored from KLIB also has no IR
		// body and no source offset; classifying it from that physical absence would skip the selected DIM declaration.
		val inheritedSynthetic = isInheritedSynthetic(fn)
		val fakeOverride = if (inheritedSynthetic) ",\"fakeOverride\":true" else ""
		val abstract = fn.modality == Modality.ABSTRACT
		val inheritedImplementation = inheritedImplementationFact(fn)
		// A concrete body-less interface declaration is meaningful only when Frontend override resolution selected an
		// inherited implementation. Do not let a missing fact become an empty CLR DIM body or invite a later hierarchy
		// search: fail at the Kotlin-semantics boundary that owns this decision.
		if (!abstract && !hasDefault && inheritedImplementation.isEmpty())
			error("concrete interface member '${iface.name}.${fn.name}' has neither a body nor a frontend-selected inherited implementation")
		return """{"name":${str(name)}${declarationIdField(fn)}${explicitClrNameField(fn)}$accessorFact,"static":false,"override":false,"virtual":true,"abstract":$abstract$fakeOverride$inheritedImplementation${typeParamsJson(fn.typeParameters)},"params":[$params],"ret":${str(ret)}${retCtxFnTypeField(fn)}${funModsJson(fn)}${resultTypeJson(fn)},"body":[$body],"attrs":[$memberAttrs]${overridesJson(fn)}}"""
	}
	val funMethods = iface.declarations.filterIsInstance<IrSimpleFunction>()
		// equals/hashCode/toString are inherited from Any into every Kotlin interface (fake overrides). On the CLR
		// System.Object already provides Equals/GetHashCode/ToString, so emitting them as interface members creates
		// abstract slots no implementer fills (the lowercase Kotlin name never binds Object's) -> TypeLoadException.
		.filterNot { it.name.asString() in setOf("equals", "hashCode", "toString") }
		// A `companion { }` member is a static of the interface, not an abstract slot: it carries a body and no
		// implementer overrides it. Emitted below alongside the interface's static fields.
		.filterNot { isKotlinStaticFunction(it) }
		// Keep inherited declarations in BIR. ifaceMethod records the frontend-selected concrete implementation as an
		// explicit inheritedImplementation fact; bir2cir then elides only declarations carrying that fact. In particular,
		// neither layer guesses DIM-ness from whether an IR/BIR body happens to be present (referenced declarations may
		// legitimately have no body). Abstract fake overrides carry no selected implementation and remain as slots.
		.map { ifaceMethod(it) }
	val propMethods = iface.declarations.filterIsInstance<IrProperty>()
		.filterNot { isKotlinStaticProperty(it) }
		.flatMap { p -> listOfNotNull(p.getter?.let { ifaceMethod(it, p) }, p.setter?.let { ifaceMethod(it, p) }) }
	// `interface I { companion { fun foo() … } }`: a CLR interface legally declares static methods with bodies, static
	// fields and a type initializer, so an interface's static members need no separate carrier — they are emitted the
	// same way a class's are.
	val staticMethods = iface.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { isKotlinStaticFunction(it) && it.correspondingPropertySymbol == null }
		.map { method(it, static = true) }
	val staticAccessors = iface.declarations.filterIsInstance<IrProperty>()
		.filter { isKotlinStaticProperty(it) }
		.flatMap { p ->
			listOfNotNull(
				p.getter?.takeIf { !p.isConst && !isClrField(p) }?.let { accessorMethod(it, p.name.asString(), true) },
				p.setter?.takeIf { !p.isConst && !isClrField(p) }?.let { accessorMethod(it, p.name.asString(), false) })
		}
	val staticFields = staticPropertyFields(iface).joinToString(",")
	val staticProps = iface.declarations.filterIsInstance<IrProperty>()
		.filter { isKotlinStaticProperty(it) && it.getter != null && !it.isConst && !isClrField(it) }
		.joinToString(",") { p ->
			val n = p.name.asString()
			"""{"name":${str(n)},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, p.setter != null)},"kotlinStatic":true}"""
		}
	// Companion declarations are separate representation-neutral types; bir2cir later materializes their nested CLR
	// carriers, including for an interface owner.
	val methods = (funMethods + propMethods).distinct().plus(staticMethods).plus(staticAccessors).joinToString(",")
	// 2B layer 1: a Kotlin interface property -> a real CLR property. The Property row carries only which accessor roles
	// exist; bir2cir allocates and links the physical MethodDefs so dll2klib later sees one property association.
	val ifaceProps = iface.declarations.filterIsInstance<IrProperty>()
		.filter { it.getter != null && !isKotlinStaticProperty(it) }.joinToString(",") { p ->
			val n = p.name.asString()
			"""{"name":${str(n)},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, p.setter != null)}}"""
		}
	val allIfaceProps = listOf(ifaceProps, staticProps).filter { it.isNotEmpty() }.joinToString(",")
	val semanticOwner = semanticOwnerJson(iface)
	val ifaces = interfaceSuperTypes(iface)
	// Round-trip class-nature facts (Kotlin, not CLR) as structured `mods` (spec §2.1): `fun interface` (SAM) and
	// `sealed` — carried so a re-consuming Kotlin module can restore them (ilemit stamps [KotlinFunInterface]/
	// [KotlinSealed]; a plain CLR interface loses both).
	val funSealed = classModsJson(fnIface = iface.isFun, sealed = iface.modality == Modality.SEALED)
	val kotlinCompanion = ""
	return """{"name":${str(typeName(iface))},"kind":"interface"$semanticOwner$funSealed${typeParamsJson(iface.typeParameters)}$kotlinCompanion,"base":null,"interfaces":[$ifaces],"fields":[$staticFields],"ctors":[],"methods":[$methods],"properties":[$allIfaceProps],"attrs":[${attrsJson(iface.annotations)}]}"""
}

private const val CLR_ENUM_ANNOTATION = "kotlin.clr.ClrEnum"

internal fun IrClass.isExplicitClrEnum(): Boolean =
	annotations.any { it.type.classFqName?.asString() == CLR_ENUM_ANNOTATION }

private fun BirEmitter.reportClrEnumError(node: IrElement?, detail: String) {
	hadError = true
	messageCollector?.report(
		CompilerMessageSeverity.ERROR,
		"malformed @ClrEnum declaration: $detail",
		locationOf(node),
	)
}

private fun BirEmitter.rejectClrEnumOnNonEnum(klass: IrClass) {
	if (klass.kind != ClassKind.ENUM_CLASS && klass.isExplicitClrEnum())
		reportClrEnumError(klass, "@ClrEnum may be applied only to an enum class")
}

private fun clrEnumConstantText(constant: IrConst, underlying: String?): String = when (underlying) {
	// Kotlin IR represents unsigned constants by the same signed primitive bit carrier used by the inline class.
	// Normalize from that carrier here, while the source-owned Kotlin type is still known, so BIR can preserve the
	// full unsigned domain without a signed JSON-number ambiguity.
	"kotlin.UByte" -> when (val value = constant.value) {
		is UByte -> value.toString()
		is Byte -> value.toUByte().toString()
		is Number -> value.toInt().toUByte().toString()
		else -> value.toString()
	}
	"kotlin.UShort" -> when (val value = constant.value) {
		is UShort -> value.toString()
		is Short -> value.toUShort().toString()
		is Number -> value.toInt().toUShort().toString()
		else -> value.toString()
	}
	"kotlin.UInt" -> when (val value = constant.value) {
		is UInt -> value.toString()
		is Int -> value.toUInt().toString()
		is Number -> value.toLong().toUInt().toString()
		else -> value.toString()
	}
	"kotlin.ULong" -> when (val value = constant.value) {
		is ULong -> value.toString()
		is Long -> value.toULong().toString()
		is Number -> value.toLong().toULong().toString()
		else -> value.toString()
	}
	else -> constant.value.toString()
}

private fun IrDeclaration.containingFile(): IrFile? {
	var owner = parent
	while (owner is IrDeclaration) owner = owner.parent
	return owner as? IrFile
}

/**
 * Evaluate exactly the constant-expression language accepted by Kotlin's frontend. The upstream IR interpreter's
 * intrinsic-constant mode admits literals, const-property reads, and Kotlin's compile-time operators/conversions;
 * arbitrary source functions remain outside that mode. Keeping this in kotc preserves the source-level distinction
 * between a constant enum argument and an ordinary expression instead of asking bir2cir to reconstruct it.
 */
private fun BirEmitter.clrEnumConstant(expression: org.jetbrains.kotlin.ir.expressions.IrExpression, owner: IrDeclaration): IrConst? {
	if (expression is IrConst) return expression
	val builtIns = irBuiltIns ?: return null
	val file = owner.containingFile() ?: return null
	val mode = EvaluationMode.OnlyBuiltins
	val admissible = runCatching {
		expression.accept(IrInterpreterCommonChecker(), IrInterpreterCheckerData(file, mode, builtIns))
	}.getOrDefault(false)
	if (!admissible) return null
	return runCatching { IrInterpreter(builtIns).interpret(expression, file) as? IrConst }.getOrNull()
}

/** A Kotlin `enum class` -> a real .NET enum (ilemit DefineEnum + literals). */
internal fun BirEmitter.enumDef(e: IrClass): String {
	val sourceEntries = e.declarations.filterIsInstance<IrEnumEntry>()
	val explicit = e.isExplicitClrEnum()
	var clrEnumFact = ""
	val entries = if (!explicit) {
		sourceEntries.mapIndexed { i, ent -> """{"name":${str(ent.name.asString())},"ordinal":$i}""" }
	} else {
		val constructors = e.declarations.filterIsInstance<IrConstructor>()
		val primary = constructors.singleOrNull { it.isPrimary }
		val parameters = primary?.parameters?.filter(::isValueParameter).orEmpty()
		val legalTypes = setOf(
			"kotlin.Byte", "kotlin.UByte", "kotlin.Short", "kotlin.UShort",
			"kotlin.Int", "kotlin.UInt", "kotlin.Long", "kotlin.ULong",
		)
		if (primary == null || parameters.size != 1)
			reportClrEnumError(e, "exactly one regular primary-constructor parameter is required")
		val parameter = parameters.singleOrNull()
		val parameterType = parameter?.type?.classFqName?.asString()
		if (parameter != null && parameterType !in legalTypes)
			reportClrEnumError(parameter, "the constructor parameter must be Byte, UByte, Short, UShort, Int, UInt, Long, or ULong")
		if (parameter?.defaultValue != null)
			reportClrEnumError(parameter, "the constructor parameter must not have a default value")
		if (parameter?.varargElementType != null)
			reportClrEnumError(parameter, "the constructor parameter must not be vararg")
		if (constructors.any { !it.isPrimary })
			reportClrEnumError(constructors.first { !it.isPrimary }, "secondary constructors are not allowed")

		val implementedInterfaces = e.superTypes.any { st ->
			(st.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE
		}
		if (implementedInterfaces)
			reportClrEnumError(e, "additional interfaces are not allowed")
		val instanceProperty = e.declarations.filterIsInstance<IrProperty>().firstOrNull { p ->
			p.origin.toString() == "DEFINED" && !p.isFakeOverride && !isKotlinStaticProperty(p)
		}
		if (instanceProperty != null)
			reportClrEnumError(instanceProperty, "the constructor parameter must be non-property and instance properties are not allowed")
		val instanceMethod = e.declarations.filterIsInstance<IrSimpleFunction>().firstOrNull {
			it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null && !isKotlinStaticFunction(it)
		}
		if (instanceMethod != null)
			reportClrEnumError(instanceMethod, "instance methods are not allowed")
		val initializer = e.declarations.filterIsInstance<IrAnonymousInitializer>().firstOrNull()
		if (initializer != null)
			reportClrEnumError(initializer, "init blocks are not allowed")
		val entryBody = sourceEntries.firstOrNull { it.correspondingClass != null }
		if (entryBody != null)
			reportClrEnumError(entryBody, "enum-entry bodies are not allowed")

		val seenValues = HashSet<String>()
		val rendered = sourceEntries.mapIndexed { i, ent ->
			val call = (ent.initializerExpression as? IrExpressionBody)?.expression as? IrEnumConstructorCall
			val args = call?.let(::regularArgs).orEmpty()
			val constant = args.singleOrNull()?.let { clrEnumConstant(it, ent) }
			if (args.size != 1 || constant == null)
				reportClrEnumError(ent, "entry '${ent.name.asString()}' must pass exactly one compile-time integral constant")
			val value = constant?.let { clrEnumConstantText(it, parameterType) } ?: "0"
			if (constant != null && !seenValues.add(value))
				reportClrEnumError(ent, "entry '${ent.name.asString()}' has duplicate physical enum value $value; aliases are not supported")
			"""{"name":${str(ent.name.asString())},"ordinal":$i,"value":${str(value)}}"""
		}
		val underlying = parameterType ?: "kotlin.Int"
		clrEnumFact = ""","clrEnum":{"underlying":${fqnJson(underlying)}}"""
		rendered
	}
	val semanticOwner = semanticOwnerJson(e)
	val visibility = visOf(e).let { if (it == "public") "" else ""","vis":${str(it)}""" }
	// The companion is emitted as its own semantic declaration; enumDef must not manufacture a duplicate association.
	val kotlinCompanion = ""
	val explicitMetadata = if (explicit) ""","attrs":[${attrsJson(e.annotations)}]${posJson(e)}""" else ""
	return """{"name":${str(typeName(e))},"kind":"enum"$semanticOwner$visibility$kotlinCompanion$clrEnumFact,"entries":[${entries.joinToString(",")}]$explicitMetadata}"""
}

/** A "rich" enum has source-authored state/behavior or per-entry bodies -> can't be a CLR enum. */
internal fun BirEmitter.isRichEnum(ec: IrClass): Boolean {
	if (ec.kind != ClassKind.ENUM_CLASS) return false
	// @ClrEnum is an exact source contract. enumDef validates it and emits either that native enum shape or a
	// source-located error; malformed input must never silently fall back to the rich reference-class representation.
	if (ec.isExplicitClrEnum()) return false
	// A CLR enum cannot implement an interface. Even a marker/default-only interface therefore requires the rich
	// class shape; checking only source members misses `enum class E : Marker { A }` because every inherited member is
	// synthetic in that declaration.
	val implementedInterfaces = ec.superTypes.any { st ->
		(st.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE && birType(st) !is TypeNode.Fn
	}
	val ctorParams = ec.declarations.filterIsInstance<IrConstructor>()
		.any { c -> c.parameters.any { it.kind == IrParameterKind.Regular } }
	val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.any { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
	val entryBodies = ec.declarations.filterIsInstance<IrEnumEntry>().any { it.correspondingClass != null }
	// Property accessors and anonymous initializers are not counted by userMethods. Either needs the plain-class shape:
	// a source property may carry instance storage or a computed accessor, while a companion property needs a
	// non-literal static field (ECMA-335 II.14.3 permits a CLR enum only one instance field and static literal fields).
	// Match source properties by their declaration origin so synthetic `entries` does not make every enum rich.
	val userProperties = ec.declarations.filterIsInstance<IrProperty>()
		.any { it.origin.toString() == "DEFINED" && !it.isFakeOverride }
	val initializers = ec.declarations.any { it is IrAnonymousInitializer }
	return implementedInterfaces || ctorParams || userMethods || entryBodies || userProperties || initializers
}

/**
 * A rich enum -> a plain class with static singleton instances (JVM-style; Codex-confirmed). Fields:
 * `__name`/`__ordinal` (Kotlin Enum metadata) + user props; per-entry `static readonly` field initialized
 * in the `.cctor`; `toString`->`__name`; `values()`->fresh array; `valueOf(name)`->linear match.
 */
internal fun BirEmitter.richEnumDef(ec: IrClass): String {
	val savedSemanticOwner = activeSemanticOwner
	activeSemanticOwner = typeName(ec)
	val name = typeName(ec)
	val entries = ec.declarations.filterIsInstance<IrEnumEntry>()
	val enumCtors = ec.declarations.filterIsInstance<IrConstructor>()
	val primaryCtor = enumCtors.first { it.isPrimary }
	val userParams = primaryCtor.parameters.filter(::isValueParameter)
	fun metadataParamNames(c: IrConstructor): Pair<String, String> {
		val used = c.parameters.filter(::isValueParameter).mapTo(HashSet()) { it.name.asString() }
		fun fresh(base: String): String {
			var candidate = base
			while (!used.add(candidate)) candidate = "_$candidate"
			return candidate
		}
		return fresh("__name") to fresh("__ordinal")
	}
	val (primaryNameParam, primaryOrdinalParam) = metadataParamNames(primaryCtor)
	// User properties follow the CLR property model exactly like typeDef: the access site emits an explicit property
	// identity and role (there is no rich-enum special case for user props — only name/ordinal route to the
	// __name/__ordinal fields), so the class must carry real accessors plus a
	// `properties` entry, with the backing field demoted to internal. A bare public field alone crashes
	// ilemit with "<Enum>.get_<prop> not found".
	// Only REAL user properties: kotlin.Enum's `name`/`ordinal` ride along as body-less fake overrides and
	// `entries` as an IrSyntheticBody getter (call sites route all three to __name/__ordinal/values());
	// emitting their accessors would produce empty methods (ilverify ReturnMissing). A source accessor is either a
	// concrete IrBlockBody or an abstract body-less declaration; no other body shape belongs in this class.
	// A `companion { }` property of an enum is a static of the enum class, not per-entry state: it keeps its own
	// storage and accessors (below) and never reaches the entry constructor.
	val userProps = ec.declarations.filterIsInstance<IrProperty>().filter { p ->
		if (isKotlinStaticProperty(p)) return@filter false
		if (!p.isFakeOverride) return@filter true
		// If every entry body implements an interface property, FIR materializes the still-unimplemented base slot as an
		// abstract fake override. Keep that declaration on the abstract enum base so each entry subclass has a CLR slot
		// to override. Concrete inherited implementations remain omitted exactly as on an ordinary class.
		listOfNotNull(p.getter, p.setter).any { accessor ->
			isInheritedSynthetic(accessor) && accessor.modality == Modality.ABSTRACT &&
				selectedInheritedImplementation(accessor) == null
		}
	}
	fun emitsAccessor(a: IrSimpleFunction?) = a != null &&
		(a.body is IrBlockBody || (a.body == null && a.modality == Modality.ABSTRACT))
	fun emitsGet(p: IrProperty) = emitsAccessor(p.getter) && !p.isConst && !p.isLateinit && !isClrField(p)
	fun emitsSet(p: IrProperty) = emitsAccessor(p.setter) && !p.isConst && !p.isLateinit && !isClrField(p)
	val userFields = userProps.mapNotNull { p ->
		val bf = p.backingField ?: return@mapNotNull null
		val visJson = if (emitsGet(p)) ""","vis":"internal"""" else ""
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()}$visJson}"""
	} + syntheticInstanceStorageFields(ec)
	val propAccessors = userProps.flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	val propsList = userProps.filter { emitsGet(it) }.joinToString(",") { p ->
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, emitsSet(p))}}"""
	}
	// ctor(__name, __ordinal, <user params>). The frontend-authored property initializers below exclusively store
	// constructor properties; a regular parameter that is not `val`/`var` has no field and remains available to later
	// property initializers and init blocks as a constructor local.
	val ctorParams = (listOf(
		"""{"name":${str(primaryNameParam)},"type":${fqnJson("kotlin.String")}}""",
		"""{"name":${str(primaryOrdinalParam)},"type":${fqnJson("kotlin.Int")}}""") +
		paramsJsonList(userParams)).joinToString(",")
	// The synthesized rich-enum constructor replaces the frontend primary constructor, so it must also expand the
	// frontend's IrInstanceInitializerCall: constructor-property storage, enum-body property initializers, and init
	// blocks retain their frontend declaration order. Entry subclasses call this constructor before their own instance
	// initializer expansion, preserving Kotlin's base-before-derived initialization order.
	val ctorBody = (listOf(
		"""{"k":"setField","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":"__name","value":{"k":"local","name":${str(primaryNameParam)}}}""",
		"""{"k":"setField","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":"__ordinal","value":{"k":"local","name":${str(primaryOrdinalParam)}}}""") +
		instanceInitializerStatements(ec)).joinToString(",")
	// Per-entry bodies (`PLUS { override fun apply(…)=… }`) become subclasses, but their presence alone does not make
	// the enum base abstract: a body-less sibling still constructs that base directly. Only an actual abstract member
	// requires an abstract base; Kotlin then requires every entry to implement it. (T A-109, #279.)
	val hasPerEntry = entries.any { it.correspondingClass != null }
	val absMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { it.correspondingPropertySymbol == null && it.body == null && it.modality == Modality.ABSTRACT }
	val hasAbstractAccessor = userProps.any { p ->
		listOfNotNull(p.getter, p.setter).any { it.body == null && it.modality == Modality.ABSTRACT }
	}
	val baseAbstract = absMethods.isNotEmpty() || hasAbstractAccessor
	// base ctor must be callable from the entry subclasses -> protected (was private for the flat form).
	val ctor = """{"params":[$ctorParams],"baseArgs":null,"thisArgs":null,"vis":${str(if (hasPerEntry) "protected" else "private")},"body":[$ctorBody]}"""
	// Every secondary enum constructor remains a real constructor on the rich class. Prefix the same synthesized
	// metadata parameters, then preserve the frontend-selected `this(...)` target and its complete argument plan.
	// This is declaration projection only: bir2cir resolves the exact physical constructor from delegationSig.
	fun secondaryCtor(c: IrConstructor): String {
		val body = c.body as? IrBlockBody
		val delegating: IrFunctionAccessExpression? = body?.statements?.firstNotNullOfOrNull { statement ->
			when (statement) {
				is IrDelegatingConstructorCall -> statement
				is IrEnumConstructorCall -> statement
				else -> null
			}
		}
		if (delegating == null || delegating.symbol.owner.parent !== ec) {
			invariantBroken(c, "a rich enum secondary constructor has no same-enum delegation target")
			return """{"params":[],"baseArgs":null,"thisArgs":null,"vis":"private","body":[]}"""
		}
		val sourceParams = c.parameters.filter(::isValueParameter)
		val (nameParam, ordinalParam) = metadataParamNames(c)
		val params = (listOf(
			"""{"name":${str(nameParam)},"type":${fqnJson("kotlin.String")}}""",
			"""{"name":${str(ordinalParam)},"type":${fqnJson("kotlin.Int")}}""") +
			paramsJsonList(sourceParams))
			.joinToString(",")
		val (plan, delegatedArgs) = withCallPlan(delegating) { delegatedCtorArgs(delegating) }
		val thisArgs = (listOf(
			"""{"k":"local","name":${str(nameParam)}}""",
			"""{"k":"local","name":${str(ordinalParam)}}""") + delegatedArgs).joinToString(",")
		val targetTypes = delegating.symbol.owner.parameters.filter(::isValueParameter)
			.map { birType(it.type).toJson() }
		val delegationSig = (listOf(fqnJson("kotlin.String"), fqnJson("kotlin.Int")) + targetTypes)
			.joinToString(",")
		// The delegated target owns instance initialization. A secondary body emits only its own statements after the
		// delegation; expanding an IrInstanceInitializerCall here would initialize the enum twice.
		val ownStatements = body.statements.mapNotNull { statement ->
			when (statement) {
				is IrDelegatingConstructorCall, is IrEnumConstructorCall, is IrInstanceInitializerCall -> null
				else -> stmt(statement)
			}
		}
		val emittedBody = (preconditionChecks(c) + ownStatements).joinToString(",")
		val bindings = plan.bindingsJson().takeIf { !plan.isEmpty }
			?.let { ""","delegationBindings":$it""" }.orEmpty()
		return """{"params":[$params],"baseArgs":null,"thisArgs":[$thisArgs],"delegationSig":[$delegationSig]$bindings,"vis":${str(if (hasPerEntry) "protected" else "private")},"body":[$emittedBody]}"""
	}
	val ctors = (listOf(ctor) + enumCtors.filter { !it.isPrimary }.map(::secondaryCtor)).joinToString(",")
	// instance fields: metadata + user props.
	val fields = (listOf(
		"""{"name":"__name","type":${fqnJson("kotlin.String")},"initOnly":true}""",
		"""{"name":"__ordinal","type":${fqnJson("kotlin.Int")},"initOnly":true}""") + userFields).toMutableList()
	// per-entry static singleton, init = new <Enum-or-entry-subclass>("NAME", ordinal, <entry ctor args>).
	val subDefs = ArrayList<String>()
	val nameOrd = { i: Int, ent: IrEnumEntry -> listOf("""{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(ent.name.asString())}}""", """{"k":"const","type":${fqnJson("kotlin.Int")},"value":$i}""") }
	entries.forEachIndexed { i, ent ->
		val cc = ent.correspondingClass
		if (cc != null) {
			// A body entry `NAME(args) { override … }` is its own subclass `<>Enum_NAME : Enum`. The enum-super
			// args (the `args`) are baked into the subclass's base() call; the entry field constructs it with
			// just (__name, __ordinal) so the subclass ctor is uniform regardless of user params.
			val sub = "<>${name}_${ent.name.asString()}"
			val superCall = enumSuperCall(cc)
			subDefs.add(enumEntrySubclass(
				sub, name, cc, superCall.args, superCall.bindings, superCall.parameterTypes))
			fields.add("""{"name":${str(ent.name.asString())},"type":${fqnJson(name)},"static":true,"initOnly":true,"init":{"k":"new","type":${fqnJson(sub)},"argTypes":[${fqnJson("kotlin.String")},${fqnJson("kotlin.Int")}],"args":[${nameOrd(i, ent).joinToString(",")}]}}""")
		} else {
			val ecc = (ent.initializerExpression as? IrExpressionBody)?.expression as? IrEnumConstructorCall
			if (ecc == null) {
				invariantBroken(ent, "a rich enum entry has no enum-constructor call")
				return@forEachIndexed
			}
			// An entry's `NAME(args)` is an omitting call site too (`R(1)` on `enum class Col(val rgb: Int, val
			// label: String = "c")`), so it fills omitted defaults like every other call shape — under its own
			// evaluation plan (§2.7), which rides a `callEval` around this initializer because a static field's
			// initializer IS an expression position.
			val (entryPlan, entryArgs) = withCallPlan(ecc) { filledArgs(ecc) }
			val newArgs = (nameOrd(i, ent) + entryArgs).joinToString(",")
			val selectedCtor = ecc.symbol.owner
			val selectedTypes = selectedCtor.parameters.filter(::isValueParameter)
				.map { birType(it.type).toJson() }
			val entrySig = (listOf(fqnJson("kotlin.String"), fqnJson("kotlin.Int")) + selectedTypes)
				.joinToString(",")
			val newEntry = """{"k":"new","type":${fqnJson(name)},"argTypes":[$entrySig],"args":[$newArgs],"memberSignature":[$entrySig]}"""
			val init = entryPlan?.wrap(newEntry, fqnJson(name)) ?: newEntry
			fields.add("""{"name":${str(ent.name.asString())},"type":${fqnJson(name)},"static":true,"initOnly":true,"init":$init}""")
		}
	}
	// methods: concrete user methods + abstract member decls + toString + values() + valueOf().
	// A `companion { }` member rides the same list, marked static from the frontend's own fact.
	val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		// A frontend-generated class-delegation forwarder owns a real body and interface slot just like a source method.
		// Other synthetic enum helpers remain excluded and are emitted explicitly below.
		.filter { (it.origin.toString() == "DEFINED" || it.origin == IrDeclarationOrigin.DELEGATED_MEMBER) &&
			it.correspondingPropertySymbol == null && it.body != null }
		.map { method(it, static = isKotlinStaticFunction(it)) } +
		absMethods.map { m -> """{"name":${str(m.name.asString())},"static":false,"override":false,"virtual":true,"abstract":true,"vis":"public","params":[${paramsJsonList(m.parameters).joinToString(",")}],"ret":${birType(m.returnType).toJson()},"body":[]}""" }
	val sf = { e: IrEnumEntry -> """{"k":"staticField","ownerType":${fqnJson(name)},"name":${str(e.name.asString())}}""" }
	val toStr = """{"name":"toString","static":false,"override":true,"virtual":true,"objectOverride":true,"vis":"public","params":[],"ret":${fqnJson("kotlin.String")},"body":[{"k":"return","value":{"k":"field","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":"__name"}}]}"""
	val valuesArr = """{"k":"newArray","elem":${fqnJson(name)},"elems":[${entries.joinToString(",") { sf(it) }}]}"""
	val valuesM = """{"name":"values","generated":true,"static":true,"override":false,"virtual":false,"vis":"public","params":[],"ret":${TypeNode.Array(TypeNode.Fqn(name)).toJson()},"body":[{"k":"return","value":$valuesArr}]}"""
	val voBranches = entries.joinToString(",") { ent ->
		"""{"cond":{"k":"objEq","lhs":{"k":"local","name":"name"},"rhs":{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(ent.name.asString())}}},"body":[{"k":"return","value":${sf(ent)}}]}"""
	}
	// Kotlin's `Enum.valueOf` throws IllegalArgumentException on an unknown name (@ClrTypeAlias System.ArgumentException).
	val voThrow = throwExpr(newExc("kotlin.IllegalArgumentException", str("No enum constant $name")))
	// With no entries there is no conditional arm: emit the failure as the method's unconditional body. Besides being
	// the faithful control-flow shape, this avoids manufacturing an `if` whose interpolated branch list starts with a
	// comma. Non-empty enums retain the ordinary linear name match followed by the same failure path.
	val voBody = if (entries.isEmpty()) {
		"""{"k":"exprStmt","expr":$voThrow}"""
	} else {
		"""{"k":"if","branches":[$voBranches,{"else":true,"body":[{"k":"exprStmt","expr":$voThrow}]}]}"""
	}
	val valueOfM = """{"name":"valueOf","generated":true,"static":true,"override":false,"virtual":false,"vis":"public","params":[{"name":"name","type":${fqnJson("kotlin.String")}}],"ret":${fqnJson(name)},"body":[$voBody]}"""
	// A `companion { }` property of an enum: static storage (initialized in the type initializer, after the entry
	// singletons declared above, which is the order Kotlin specifies) plus its own static accessors and CLR property.
	val staticProps = ec.declarations.filterIsInstance<IrProperty>().filter { isKotlinStaticProperty(it) }
	fun emitsStaticGet(p: IrProperty) = p.getter?.body is IrBlockBody && !p.isConst && !p.isLateinit && !isClrField(p)
	fun emitsStaticSet(p: IrProperty) = p.setter?.body is IrBlockBody && !p.isConst && !p.isLateinit && !isClrField(p)
	fields.addAll(staticPropertyFields(ec))
	val staticPropAccessors = staticProps.flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsStaticGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsStaticSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	val staticPropsList = staticProps.filter { emitsStaticGet(it) }.joinToString(",") { p ->
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, emitsStaticSet(p))},"kotlinStatic":true}"""
	}
	val allPropsList = listOf(propsList, staticPropsList).filter { it.isNotEmpty() }.joinToString(",")
	val methods = (userMethods + propAccessors + staticPropAccessors + listOf(toStr, valuesM, valueOfM)).joinToString(",")
	val ifaces = interfaceSuperTypes(ec)
	// As for an ordinary class, inherited default members are frontend-selected implementations. Preserve that
	// choice so bir2cir can allocate an exact MethodImpl when an unrelated CLR-shaped method would otherwise capture
	// the interface slot.
	val inheritedDefaultAccessors = ec.declarations.filterIsInstance<IrProperty>().flatMap { p ->
		listOfNotNull(
			inheritedDefaultAccessorFact(p, p.getter, "get"),
			inheritedDefaultAccessorFact(p, p.setter, "set"))
	}
	val inheritedDefaultMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.mapNotNull(::inheritedDefaultMethodFact)
	val inheritedDefaultsJson = if (inheritedDefaultAccessors.isEmpty()) "" else
		""","inheritedDefaultAccessors":[${inheritedDefaultAccessors.joinToString(",")}]"""
	val inheritedDefaultMethodsJson = if (inheritedDefaultMethods.isEmpty()) "" else
		""","inheritedDefaultMethods":[${inheritedDefaultMethods.joinToString(",")}]"""
	// `enumRich:true` — a FAITHFUL "this class originated from a Kotlin enum" fact (not a CLR-shape decision), so
	// bir2cir's EnumIntrinsicLowering can lower `enumValues<ThisEnum>()` to the synthesized static values()/valueOf()
	// rather than the System.Enum-reflection semantic node (a rich enum is a plain class, invisible to that reflection).
	// `richEnum` is the durable round-trip declaration fact: it explicitly relates each source entry to its physical
	// singleton field, the two instance metadata slots, and the two compiler-generated physical APIs. A downstream
	// compiler must not reconstruct
	// enum meaning from those members' spellings or signatures. richEnumDef likewise does not flatten a companion's
	// declarations into the enum class.
	val richEnumEntries = entries.joinToString(",") { ent ->
		"""{"name":${str(ent.name.asString())},"field":${str(ent.name.asString())}}"""
	}
	val richEnum = ""","richEnum":{"entries":[$richEnumEntries],"name":"__name","ordinal":"__ordinal","values":"values","valueOf":"valueOf"}"""
	val kotlinCompanion = ""
	val baseDef = """{"name":${str(name)},"kind":"class","enumRich":true,"abstract":$baseAbstract,"vis":${str(visOf(ec))}${semanticOwnerJson(ec)}$kotlinCompanion,"base":null,"interfaces":[$ifaces],"fields":[${fields.joinToString(",")}],"ctors":[$ctors],"methods":[$methods],"properties":[$allPropsList]$inheritedDefaultsJson$inheritedDefaultMethodsJson$richEnum}"""
	// Emit the base enum class first, then each per-entry subclass.
	val result = (listOf(baseDef) + subDefs).joinToString(",")
	activeSemanticOwner = savedSemanticOwner
	return result
}

internal data class EnumSuperCall(
	val args: List<String>,
	val bindings: String?,
	val parameterTypes: List<String>)

/** The exact enum-super call a per-entry body's anonymous subclass makes. Omitted defaults are filled like every
 * constructor call site, while the selected constructor's source parameter types preserve overload identity after
 * the synthesized name/ordinal parameters. These args ride the subclass ctor's `baseArgs`, a DECLARATION slot with
 * no wrapping expression, so the call plan rides `delegationBindings`; bir2cir lowers it to `preStmts` that ilemit
 * emits ahead of the base call. */
internal fun BirEmitter.enumSuperCall(cc: IrClass): EnumSuperCall {
	val ctor = cc.declarations.filterIsInstance<IrConstructor>().firstOrNull()
	if (ctor == null) {
		invariantBroken(cc, "a rich enum entry body has no constructor")
		return EnumSuperCall(emptyList(), null, emptyList())
	}
	val call = (ctor.body as? IrBlockBody)?.statements?.firstNotNullOfOrNull { it as? IrEnumConstructorCall }
	if (call == null) {
		invariantBroken(ctor, "a rich enum entry body has no enum-constructor call")
		return EnumSuperCall(emptyList(), null, emptyList())
	}
	val (plan, args) = withCallPlan(call) { filledArgs(call) }
	val parameterTypes = call.symbol.owner.parameters.filter(::isValueParameter)
		.map { birType(it.type).toJson() }
	return EnumSuperCall(args, plan.bindingsJson().takeIf { !plan.isEmpty }, parameterTypes)
}

/** Instance storage authored by a Kotlin class body. Rich-enum entry subclasses and ordinary classes share this
 * declaration rule; only their constructor/base-call shapes differ. */
internal fun BirEmitter.instanceStorageFields(klass: IrClass): List<String> {
	val propertyFields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { p ->
		// A `by clrEvent()` property's fictional ClrEvent<T> field is replaced by the event synthesis, and a companion
		// property's storage belongs to the enclosing type rather than each instance.
		if (clrEventDelegateCall(p) != null || hasStaticPropertyStorage(klass, p)) return@mapNotNull null
		val bf = p.backingField ?: return@mapNotNull null
		// Accessor-routed storage is an implementation detail. @ClrField / const / lateinit retain their declared
		// visibility; a non-publicly-settable plain field remains read-only to a re-consuming Kotlin module.
		val routed = p.getter != null && !p.isConst && !p.isLateinit && !isClrField(p)
		val visibility = if (routed) "private" else visOf(p)
		val visJson = if (visibility != "public") ""","vis":${str(visibility)}""" else ""
		val readOnly = if (!routed && (!p.isVar || (p.setter != null && visOf(p.setter!!) != "public")))
			""","readOnly":true""" else ""
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()}$visJson$readOnly${lateinitFieldFlag(p)}${volatileFieldFlag(p)}}"""
	}
	// Standalone frontend-synthesized fields (chiefly class-delegation storage) follow the same instance-initializer
	// expansion below as property backing fields.
	return propertyFields + syntheticInstanceStorageFields(klass)
}

/** Frontend-owned instance storage not associated with a Kotlin property, chiefly a `by`-delegation receiver. */
private fun BirEmitter.syntheticInstanceStorageFields(klass: IrClass): List<String> =
	klass.declarations.filterIsInstance<IrField>()
		.filter { it.correspondingPropertySymbol == null && !it.isStatic }
		.map { f ->
			val visibility = visOf(f)
			val visJson = if (visibility != "public") ""","vis":${str(visibility)}""" else ""
			val readOnly = if (f.isFinal) ""","readOnly":true""" else ""
			"""{"name":${str(f.name.asString())},"type":${birType(f.type).toJson()}$visJson$readOnly}"""
		}

/** Expand one frontend [IrInstanceInitializerCall] in declaration order. The caller owns constructor delegation and
 * installs the concrete BIR type identity before invoking this helper. */
internal fun BirEmitter.instanceInitializerStatements(klass: IrClass): List<String> {
	val statements = ArrayList<String>()
	klass.declarations.forEach { d ->
		when (d) {
			is IrProperty -> if (clrEventDelegateCall(d) == null && !hasStaticPropertyStorage(klass, d)) {
				d.backingField?.let { field -> field.initializer?.let { initializer ->
					val expression = (initializer as IrExpressionBody).expression
					statements.add(withClonedLocalFunctionIds(expression) {
						"""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(field.name.asString())},"value":${expr(expression)}}"""
					})
				} }
			}
			is IrField -> if (d.correspondingPropertySymbol == null && !d.isStatic) d.initializer?.let { initializer ->
				val expression = (initializer as IrExpressionBody).expression
				statements.add(withClonedLocalFunctionIds(expression) {
					"""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(d.name.asString())},"value":${expr(expression)}}"""
				})
			}
			is IrAnonymousInitializer -> withClonedLocalFunctionIds(d) {
				(d.body as? IrBlockBody)?.statements?.forEach { statements.add(stmt(it)) }
			}
			else -> {}
		}
	}
	return statements
}

/** A per-entry enum body `NAME(args) { override fun … }` -> a subclass `<>Enum_NAME : Enum` whose ctor takes only
 *  (__name, __ordinal) and forwards them plus the baked-in `args` to the base ctor; carries overriding members. */
internal fun BirEmitter.enumEntrySubclass(subName: String, baseName: String, cc: IrClass,
		userArgs: List<String>, delegationBindings: String?, baseParamTypes: List<String>): String {
	// The frontend's anonymous entry class has no stable source name. Pin every member-body owner reference to the
	// explicit BIR subclass identity chosen above, just as lifted anonymous classes do before emitting their bodies.
	anonNames[cc] = subName
	checkUnimplementedClrEvents(cc)
	val (clrEventBackings, clrEventMethods) = synthClrEvents(cc)
	val overrides = cc.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { it.body != null && it.correspondingPropertySymbol == null }
		.map { method(it, static = false, semanticOwnerOverride = subName) }
	val entryProps = cc.declarations.filterIsInstance<IrProperty>().filter { !it.isFakeOverride }
	fun emitsGet(p: IrProperty) = p.getter?.body is IrBlockBody && !p.isConst && !p.isLateinit &&
		!isClrField(p) && !isClrEventProperty(p)
	fun emitsSet(p: IrProperty) = p.setter?.body is IrBlockBody && !p.isConst && !p.isLateinit &&
		!isClrField(p) && !isClrEventProperty(p)
	val accessors = entryProps.flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	val properties = entryProps.filter { emitsGet(it) }.joinToString(",") { p ->
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, emitsSet(p))}${overridesJson(p.getter!!)}}"""
	}
	val fields = instanceStorageFields(cc).joinToString(",")
	val savedSemanticOwner = activeSemanticOwner
	activeSemanticOwner = subName
	val ctorBody = ArrayList<String>()
	(cc.declarations.filterIsInstance<IrConstructor>().firstOrNull()?.body as? IrBlockBody)
		?.statements?.forEach { statement ->
			when (statement) {
				is IrEnumConstructorCall, is IrDelegatingConstructorCall -> {}
				is IrInstanceInitializerCall -> ctorBody.addAll(instanceInitializerStatements(cc))
				else -> ctorBody.add(stmt(statement))
			}
		}
	activeSemanticOwner = savedSemanticOwner
	val baseArgs = (listOf("""{"k":"local","name":"__name"}""", """{"k":"local","name":"__ordinal"}""") + userArgs).joinToString(",")
	val delegationSig = (listOf(fqnJson("kotlin.String"), fqnJson("kotlin.Int")) +
		baseParamTypes).joinToString(",")
	val bindings = delegationBindings?.let { ""","delegationBindings":$it""" } ?: ""
	val subCtor = """{"params":[{"name":"__name","type":${fqnJson("kotlin.String")}},{"name":"__ordinal","type":${fqnJson("kotlin.Int")}}],"baseArgs":[$baseArgs],"thisArgs":null,"delegationSig":[$delegationSig]$bindings,"vis":"public","body":[${ctorBody.joinToString(",")}]}"""
	val clrEventsJson = if (clrEventBackings.isEmpty()) "" else
		""","clrEvents":[${clrEventBackings.joinToString(",")}]"""
	// An enum-entry body is an anonymous subclass semantically owned by the enum declaration. Keep that fact explicit;
	// bir2cir chooses its physical nesting just like it does for an object expression or local class.
	return """{"name":${str(subName)},"kind":"class","generated":true,"abstract":false,"vis":"public","semanticOwner":${str(baseName)},"base":${fqnJson(baseName)},"interfaces":[],"fields":[$fields],"ctors":[$subCtor],"methods":[${(overrides + accessors + clrEventMethods).joinToString(",")}],"properties":[$properties]$clrEventsJson}"""
}

/** Nested non-inner user classes inside [c] (recursively); excludes companion/inner/anonymous/@Clr. */
internal fun BirEmitter.nestedClasses(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { !it.isCompanion && !isExternalNetType(it) && it.name.asString() != "<no name provided>" }
		.forEach {
			if (it.kind == ClassKind.CLASS && !it.isInner) out.add(it)
			out.addAll(nestedClasses(it))
		}
	c.declarations.filterIsInstance<IrClass>().filter { it.isCompanion }.forEach { out.addAll(nestedClasses(it)) }
	return out
}

/** Nested singleton objects inside a class/object/interface (`TimeSource.Monotonic`). */
internal fun BirEmitter.nestedObjects(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { !it.isCompanion && !isExternalNetType(it) && it.name.asString() != "<no name provided>" }
		.forEach {
			if (it.kind == ClassKind.OBJECT) out.add(it)
			out.addAll(nestedObjects(it))
		}
	c.declarations.filterIsInstance<IrClass>().filter { it.isCompanion }.forEach { out.addAll(nestedObjects(it)) }
	return out
}

/** Nested enum classes inside a class/object/interface (`Base64.PaddingOption`). */
internal fun BirEmitter.nestedEnums(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { !isExternalNetType(it) && it.name.asString() != "<no name provided>" }
		.forEach { if (it.kind == ClassKind.ENUM_CLASS) out.add(it); out.addAll(nestedEnums(it)) }
	return out
}

/** Nested interfaces (recursively) inside a class OR interface (`TimeSource.WithComparableMarks`); emitted as real
 *  nested types so a supertype reference to the bare name resolves. */
internal fun BirEmitter.nestedInterfaces(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { !isExternalNetType(it) && it.name.asString() != "<no name provided>" }
		.forEach { if (it.kind == ClassKind.INTERFACE) out.add(it); out.addAll(nestedInterfaces(it)) }
	return out
}

/** `inner class`es nested (recursively) inside a class -> flattened to top-level synthetic types. */
internal fun BirEmitter.innerClasses(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { it.kind == ClassKind.CLASS && !it.isCompanion && !isExternalNetType(it) && it.name.asString() != "<no name provided>" }
		.forEach { if (it.isInner) out.add(it); out.addAll(innerClasses(it)) }
	return out
}

/** Enclosing parameters in emitted CLR type-frame order: outermost owner first. */
internal fun BirEmitter.innerEnclosingTypeParams(klass: IrClass): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
	if (!klass.isInner) return emptyList()
	val result = mutableListOf<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	var p = klass.parent as? IrClass
	while (p != null) { result.addAll(0, p.typeParameters); p = if (p.isInner) p.parent as? IrClass else null }
	return result
}

/** Enclosing parameters in Kotlin inner-application order: immediate owner first, then each owner moving outward. */
internal fun BirEmitter.innerSemanticEnclosingTypeParams(
	klass: IrClass,
): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
	if (!klass.isInner) return emptyList()
	val result = mutableListOf<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	var child = klass
	while (child.isInner) {
		val parent = child.parent as? IrClass ?: break
		result.addAll(parent.typeParameters)
		child = parent
	}
	return result
}

internal fun BirEmitter.innerClassDef(inner: IrClass): String {
	val outerThis = (inner.parent as? IrClass)?.thisReceiver
		?: return typeDef(inner)   // not actually inner-of-class; emit plainly
	// An inner class can name every enclosing instance, not just its immediate owner. Author the complete receiver
	// chain while rendering the declaration: Leaf.this.__outer reaches Middle, and another __outer reaches Outer.
	// Each edge is an explicit Kotlin inner relation; bir2cir later supplies the constructed CLR owner TypeSpecs.
	val saved = java.util.IdentityHashMap<IrValueDeclaration, String?>()
	var child = inner
	var receiver = """{"k":"this"}"""
	while (child.isInner) {
		val parent = child.parent as? IrClass ?: break
		receiver = """{"k":"field","ownerType":${fqnJson(typeName(child))},"recv":$receiver,"name":"__outer"}"""
		parent.thisReceiver?.let {
			saved[it] = captureSubst[it]
			captureSubst[it] = receiver
		}
		child = parent
	}
	return try {
		typeDef(inner, listOf(outerThis to "__outer"))
	} finally {
		for ((declaration, prior) in saved) {
			if (prior != null) captureSubst[declaration] = prior else captureSubst.remove(declaration)
		}
	}
}

/** `@kotlin.clr.ClrField` opt-out: emit this property as a plain (public) CLR FIELD, no accessor/property. */
internal fun BirEmitter.isClrField(p: IrProperty): Boolean =
	p.annotations.any { it.type.classFqName?.asString() == "kotlin.clr.ClrField" }

/** `@kotlin.concurrent.Volatile` on a `var`'s backing field: a pure Kotlin-language fact (like `suspend`/
 *  `@Synchronized`, NOT a `@Clr*` binding). Emit a `"volatile":true` FIELD flag; bir2cir threads it through and
 *  ilemit lowers it to a CLR volatile field (`modreq(IsVolatile)` + `volatile.` prefix — the C# `volatile` shape).
 *  Matched by the field's OR the property's annotations (the FIELD-targeted annotation can land on either IR node). */
internal fun BirEmitter.isVolatile(p: IrProperty): Boolean {
	fun hasVol(anns: List<IrConstructorCall>) =
		anns.any { it.type.classFqName?.asString() == "kotlin.concurrent.Volatile" }
	return hasVol(p.annotations) || (p.backingField?.let { hasVol(it.annotations) } ?: false)
}

/** `,"volatile":true` field-flag fragment (empty when not volatile). */
internal fun BirEmitter.volatileFieldFlag(p: IrProperty): String = if (isVolatile(p)) ""","volatile":true""" else ""

/** `,"lateinit":true` Kotlin field fact. bir2cir folds it into trusted metadata so dll2klib restores the standard
 *  IS_LATEINIT property flag; the physical null-check remains an access-site `lateinitGet`. */
internal fun BirEmitter.lateinitFieldFlag(p: IrProperty): String = if (p.isLateinit) ""","lateinit":true""" else ""

/** The standard KLIB IS_LATEINIT flag can live on the lazy FIR declaration even when Kotlin 2.4's static-property
 * fake-override wrapper does not copy it onto the materialized IrProperty. Read the declaration-owned fact from
 * either representation; this is still Kotlin metadata, not a CLR-layout inference. */
internal fun BirEmitter.isLateinitProperty(p: IrProperty): Boolean =
	p.isLateinit ||
		p.annotations.any { it.type.classFqName?.asString() == "kotlin.clr.ClrLateinitField" } ||
		(p as? org.jetbrains.kotlin.fir.lazy.Fir2IrLazyProperty)?.fir?.isLateInit == true

/** A property whose type is `kotlin.clr.ClrEvent<T>` — the compile-time-only fiction surfacing a .NET event.
 *  A .NET event is consumed via `subscribe` and is NEVER a first-class value or a real inherited property, so
 *  such a property must never be emitted as a member. This matters for a FAKE-OVERRIDE: when a Kotlin class
 *  subclasses a .NET type whose interface carries an event (`class MyApp : Avalonia.Application`, whose bases
 *  implement an event-bearing interface), fir2ir synthesizes a fake-override getter returning `ClrEvent<T>`;
 *  declaring it would emit an accessor/property over the un-emittable `kotlin.clr.ClrEvent` type — skip it. */
internal fun BirEmitter.isClrEventProperty(p: IrProperty): Boolean =
	p.getter?.returnType?.classFqName?.asString() == "kotlin.clr.ClrEvent"

/** The `clrEvent()` delegate-initializer call of a `by clrEvent()` property (§4.2/§5 of design-clr-event-model.md), or
 *  null. A DELEGATED property whose delegate expression is a call to `kotlin.clr.clrEvent` — the marker "synthesize the
 *  field-like .NET event impl here" (a backing delegate field + add_/remove_/raise_ accessors). Distinguished from the
 *  ELIDE case (isClrEventProperty, a CONCRETE fake-override inherited from a .NET base) by BEING delegated with this
 *  initializer: a fake-override is not delegated and has no clrEvent() initializer. */
internal fun BirEmitter.clrEventDelegateCall(p: IrProperty): IrCall? {
	if (!p.isDelegated) return null
	val init = (p.backingField?.initializer as? IrExpressionBody)?.expression as? IrCall ?: return null
	return if (init.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.clr.clrEvent") init else null
}

/** #187 missing-`by clrEvent()` diagnostic (kotc EMISSION time — the frontend can't enforce it via an abstract member:
 *  a .NET base that explicitly implements an interface event with a different-signature same-name public event can never
 *  satisfy an abstract slot, so an abstract interface event member would wrongly break `class MyApp : Avalonia.Application`).
 *  A class DIRECTLY implementing a .NET interface event must write `override val E by clrEvent()` (a DECLARED delegated
 *  property, not a fake-override) — otherwise it emits an invalid type (missing add_/remove_ -> TypeLoadException). Flag
 *  the unsatisfied case: a `ClrEvent<T>` FAKE-OVERRIDE provided by NEITHER a base CLASS that declares it (a Kotlin base
 *  that synthesized it, e.g. `PersonViewModel : ViewModelBase()`) NOR an external .NET base class (`MyApp :
 *  Avalonia.Application` — the .NET base implements its interfaces at the CLR level). Never a false positive; the sole gap
 *  is a false-NEGATIVE for `class X : UnrelatedNetBase(), IEvented` (kotc-purity forbids reading .NET metadata to know
 *  which interface a .NET base implements). */
internal fun BirEmitter.checkUnimplementedClrEvents(klass: IrClass) {
	val hasNetBaseClass = klass.superTypes.any { st ->
		(st.classifierOrNull?.owner as? IrClass)?.let { it.kind == ClassKind.CLASS && isExternalNetType(it) } == true
	}
	if (hasNetBaseClass) return
	for (p in klass.declarations.filterIsInstance<IrProperty>()) {
		if (!isClrEventProperty(p) || !p.isFakeOverride) continue
		// Provided by a base CLASS that DECLARES it (a Kotlin base that synthesized the event) -> inherited, OK.
		if ((p.getter?.resolveFakeOverride()?.parent as? IrClass)?.kind == ClassKind.CLASS) continue
		hadError = true
		messageCollector?.report(CompilerMessageSeverity.ERROR,
			"class '${klass.name.asString()}' implements a .NET interface event '${p.name.asString()}' but does not provide it; " +
				"declare `override val ${p.name.asString()} by clrEvent()` to synthesize the event (add/remove/raise accessors)",
			locationOf(klass))
	}
}

/** Synthesize the field-like .NET event impl for every `override val E by clrEvent()` (implement an interface slot) or
 *  `val E: ClrEvent<D> by clrEvent()` (declare a NEW event) on [klass] (§4.2). Returns (clrEvents backing-directive JSONs,
 *  synthesized add_/remove_/raise_ method JSONs). kotc emits PURE-KOTLIN identities only: the backing directive carries
 *  the handler Kotlin FUNCTION type; the accessors carry tagged bodies (`clrEventAccessor`) + an `overrides` closure
 *  naming the interface event slot by Kotlin identity (owner FQN + event name). bir2cir's ClrEventImplBinding resolves the
 *  concrete delegate `D` + the interface accessor slots off the ref.dll and rewrites the tagged bodies. */
internal fun BirEmitter.synthClrEvents(klass: IrClass): Pair<List<String>, List<String>> {
	val backings = ArrayList<String>()
	val methods = ArrayList<String>()
	val unit = fqnJson("kotlin.Unit")
	for (p in klass.declarations.filterIsInstance<IrProperty>()) {
		clrEventDelegateCall(p) ?: continue
		val name = p.name.asString()
		// `var E by clrEvent()` is a hard error — an event is a read-only handle (subscribe/unsubscribe/raise, never reassign).
		if (p.isVar) {
			hadError = true
			messageCollector?.report(CompilerMessageSeverity.ERROR,
				"an event must be declared `val`, not `var` (`val $name by clrEvent()`) — an event handle is subscribed/raised, never reassigned",
				locationOf(p))
			continue
		}
		val handlerArg = (p.getter?.returnType as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection
		if (handlerArg?.type == null) {
			hadError = true
			messageCollector?.report(CompilerMessageSeverity.ERROR,
				"cannot infer the event handler type for `val $name by clrEvent()`; annotate it `ClrEvent<(…) -> Unit>`",
				locationOf(p))
			continue
		}
		val handlerJson = birType(handlerArg.type).toJson()
		// The `overrides` closure: the interface event slot(s) this member implements, by Kotlin identity (owner FQN +
		// event name). bir2cir derives the concrete add_/remove_ slot + delegate `D` off the ref.dll from it. Empty for a
		// NEW event (no override), where bir2cir instead maps the handler function type to a delegate.
		val overriddenOwners = p.overriddenSymbols.mapNotNull { (it.owner.parent as? IrClass)?.fqNameWhenAvailable?.asString() }
		fun overridesFor(kind: String) = if (overriddenOwners.isEmpty()) "" else
			""","overrides":[${overriddenOwners.joinToString(",") { o -> """{"owner":${fqnJson(o)},"member":${str(name)},"kind":${str(kind)},"arity":1}""" }}]"""
		fun accessor(mname: String, virtual: Boolean, kind: String, ov: String) =
			"""{"name":${str(mname)},"static":false,"override":false,"virtual":$virtual,"abstract":false,"objectOverride":false,"vis":"public","params":[{"name":"value","type":$handlerJson}],"ret":$unit,"body":[{"k":"clrEventAccessor","kind":${str(kind)},"event":${str(name)}}]$ov}"""
		backings.add("""{"k":"clrEventBacking","name":${str(name)},"handlerType":$handlerJson}""")
		// add_/remove_: public VIRTUAL NEWSLOT (implicitly implements the interface add_/remove_ slot; bir2cir also wires the
		// explicit MethodImpl). bir2cir rewrites the `value` param type to the concrete delegate `D` + the body to the CAS loop.
		methods.add(accessor("add_$name", virtual = true, kind = "add", ov = overridesFor("event-add")))
		methods.add(accessor("remove_$name", virtual = true, kind = "remove", ov = overridesFor("event-remove")))
		// raise_: public NON-virtual (not an interface member; the raise-from-outside deviation, §6). bir2cir sets its params
		// to the delegate's Invoke params + the body to `field?.Invoke(args)`; the `value` placeholder param is overwritten.
		methods.add(accessor("raise_$name", virtual = false, kind = "raise", ov = ""))
	}
	return backings to methods
}

/** #186 — a CLR interface event reached through Kotlin class delegation (`class A : B by c`) is represented in IR as
 *  a DELEGATED_MEMBER `ClrEvent<T>` property whose getter forwards to the synthesized `$$delegate_N` field. A CLR event
 *  is not a real Kotlin getter, so emit add_/remove_ declaration shells plus a pure BIR forwarding directive instead.
 *  The directive carries only facts already present in Kotlin IR; bir2cir resolves the CLR delegate/accessors. */
internal fun BirEmitter.synthClrEventForwarders(klass: IrClass): Pair<List<String>, List<String>> {
	val forwards = ArrayList<String>()
	val methods = ArrayList<String>()
	val unit = fqnJson("kotlin.Unit")
	for (p in klass.declarations.filterIsInstance<IrProperty>()) {
		if (p.origin != IrDeclarationOrigin.DELEGATED_MEMBER || !isClrEventProperty(p)) continue
		val getter = p.getter ?: continue
		val ret = (getter.body as? IrBlockBody)?.statements?.singleOrNull() as? IrReturn ?: continue
		val targetCall = ret.value as? IrCall ?: continue
		val target = dispatchReceiver(targetCall) ?: continue
		val handlerArg = (getter.returnType as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection
		val handlerType = handlerArg?.type ?: continue
		val name = p.name.asString()
		val handlerJson = birType(handlerType).toJson()
		val overriddenOwners = p.overriddenSymbols.mapNotNull {
			(it.owner.parent as? IrClass)?.fqNameWhenAvailable?.asString()
		}
		fun overridesFor(kind: String) = if (overriddenOwners.isEmpty()) "" else
			""","overrides":[${overriddenOwners.joinToString(",") { owner ->
				"""{"owner":${fqnJson(owner)},"member":${str(name)},"kind":${str(kind)},"arity":1}"""
			}}]"""
		fun accessor(kind: String) =
			"""{"name":${str("${kind}_$name")},"static":false,"override":false,"virtual":true,"abstract":false,"objectOverride":false,"vis":"public","params":[{"name":"value","type":$handlerJson}],"ret":$unit,"body":[{"k":"clrEventAccessor","kind":${str(kind)},"event":${str(name)}}]${overridesFor("event-$kind")}}"""

		forwards.add(
			"""{"name":${str(name)},"ownerType":${birType(target.type).toJson()},"recv":${expr(target)}}""")
		methods.add(accessor("add"))
		methods.add(accessor("remove"))
	}
	return forwards to methods
}

/** STEP-1 (kotc->bir2cir clrName migration) — a PURE-KOTLIN override marker for an emitted member: the transitive
 *  closure of interface/base members it overrides, each as {owner FQN, Kotlin member name, kind, arity}. NO CLR
 *  knowledge (no @ClrIntrinsic read, no BCL name). bir2cir (Step 2) consumes this + the ref.dll @ClrIntrinsic to
 *  derive the BCL slot name. Behavior-neutral: bir2cir strips
 *  the `overrides` key, so it never reaches ilemit (Step 1 keeps CIR byte-identical). `member` is the property name
 *  for an accessor (kind getter/setter) so bir2cir can resolve the external Property/MethodSemantics slot. */
private fun BirEmitter.overrideOwnerType(fn: IrSimpleFunction, owner: IrClass): TypeNode {
	val currentOwner = (fn.parent as? IrClass)
		?: (fn.correspondingPropertySymbol?.owner?.parent as? IrClass)
	val directOwner = currentOwner?.superTypes?.firstOrNull { superType ->
		val superClass = superType.classifierOrNull?.owner as? IrClass
		superClass != null && (superClass === owner || typeName(superClass) == typeName(owner))
	}
	val instantiatedOwner = directOwner
		?: currentOwner?.defaultType?.let { correspondingSupertypeInstantiation(it, owner) }
	// The constructed supertype is a frontend type-system fact. Preserve it when available so bir2cir can distinguish
	// CLR generic definitions that share a source-facing FQN and can compare the inherited declaration in the
	// overriding class's type frame. A missing type-system answer stays a bare identity; do not invent type arguments.
	// Use the frontend's constructed supertype itself. External IR stubs may not expose their declaration type
	// parameters even though the subclass supertype carries concrete arguments; ownerSpec intentionally treats such
	// declarations as non-generic and would discard those arguments.
	return instantiatedOwner?.let { birType(it) }
		?: TypeNode.Fqn(owner.fqNameWhenAvailable?.asString()
			?: error("override owner '${owner.name}' has no Kotlin qualified name"))
}

private fun BirEmitter.overrideOwnerJson(fn: IrSimpleFunction, owner: IrClass): String =
	overrideOwnerType(fn, owner).toJson()

internal fun BirEmitter.overridesJson(fn: IrSimpleFunction): String {
	val prop = fn.correspondingPropertySymbol?.owner
	val items = if (prop != null) {
		// An ACCESSOR: walk the PROPERTY's override closure (the setter of a `var size` overriding a `val size` has
		// NO own overriddenSymbols, but the PROPERTY overrides — so use the property chain, tagged with this accessor's
		// kind). bir2cir resolves that semantic identity against exact property metadata.
		val kind = if (fn === prop.getter) "getter" else "setter"
		val ordered = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrProperty>()
		fun walkP(p: org.jetbrains.kotlin.ir.declarations.IrProperty) { for (ov in p.overriddenSymbols) { val o = ov.owner; if (ordered.add(o)) walkP(o) } }
		walkP(prop)
		// `arity` is the count of EMITTED parameters of this accessor — `[__self?] + contexts + regulars` ([isValueParameter],
		// the same sequence `accessorMethod`/`topLevelAccessorMethod` lay out) — because every consumer compares it against a
		// physical parameter count (DeclarationRename compares it against the @ClrIntrinsic arity). A plain `val`
		// override still reports 0 and a plain `var`
		// setter 1, exactly as the emitted accessors have.
		val accArity = emittedParamCount(fn)
		ordered.mapNotNull { p -> (p.parent as? IrClass)?.let { owner ->
			val targetAccessor = if (kind == "getter") p.getter else p.setter
			"""{"owner":${overrideOwnerJson(fn, owner)},"member":${str(p.name.asString())},"kind":${str(kind)},"arity":$accArity${targetAccessor?.let { declarationIdField(it) }.orEmpty()}}""" } }
	} else {
		val ordered = LinkedHashSet<IrSimpleFunction>()
		fun walk(f: IrSimpleFunction) { for (ov in f.overriddenSymbols) { val o = ov.owner; if (ordered.add(o)) walk(o) } }
		walk(fn)
		ordered.mapNotNull { m -> (m.parent as? IrClass)?.let { owner ->
			"""{"owner":${overrideOwnerJson(fn, owner)},"member":${str(m.name.asString())},"kind":"method","arity":${emittedParamCount(m)}${declarationIdField(m)}}""" } }
	}
	return if (items.isEmpty()) "" else ""","overrides":[${items.joinToString(",")}]"""
}

/** A top-level property's static accessor declaration in Kotlin vocabulary (extension receiver -> `__self`).
 *  Used for extension properties (`val T.p`) and computed top-level properties (no backing field). */
internal fun BirEmitter.topLevelAccessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
	val extRecv = extensionReceiverParam(acc)
	if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
	val savedDelegatedAccessor = activeDelegatedAccessor
	activeDelegatedAccessor = acc.correspondingPropertySymbol?.owner?.takeIf { it.isDelegated }
	// #6 non-null parameter PRECONDITIONS + getter return POSTCONDITION, gated on the accessor's real IR visibility.
	val bodyStmts = withReturnPostcondition(acc) { (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	val body = (preconditionChecks(acc) + listOfNotNull(bodyStmts.takeIf { it.isNotEmpty() })).joinToString(",")
	activeDelegatedAccessor = savedDelegatedAccessor
	if (extRecv != null) selfSubst.remove(extRecv)
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val ps = (listOfNotNull(selfParam) + paramsJsonList(acc.parameters)).joinToString(",")
	val kind = if (isGetter) "get" else "set"
	val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
	val property = acc.correspondingPropertySymbol?.owner
		?: error("top-level accessor '$propName' has no corresponding property")
	return """{"name":${str(propName)}${declarationIdField(acc)}${explicitClrNameField(acc)}${propertyAccessorFact(property, kind)},"static":true,"override":false,"virtual":false,"abstract":false,"objectOverride":false,"vis":${str(visOf(acc))}${typeParamsJson(acc.typeParameters)}${companionReceiverField(acc, kind, propName)},"params":[$ps],"ret":${str(ret)}${retCtxFnTypeField(acc)}${funModsJson(acc)},"body":[$body]}"""
}

internal fun BirEmitter.accessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
	val savedSemanticOwner = activeSemanticOwner
	activeSemanticOwner = semanticOwnerName(acc)
	val kind = if (isGetter) "get" else "set"
	// A MEMBER extension property (`class C { val T.p get() }`) has BOTH a dispatch and an extension receiver -> the
	// extension receiver rides a leading `__self` param (mirrors a member extension function); body refs to it
	// resolve via selfSubst (by identity, so it isn't confused with the dispatch `<this>`).
	val extRecv = extensionReceiverParam(acc)
	if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val savedDelegatedAccessor = activeDelegatedAccessor
	activeDelegatedAccessor = acc.correspondingPropertySymbol?.owner?.takeIf { it.isDelegated }
	// [isValueParameter], not `Regular`: `context(c: Ctx) val C.p get() = c.n` carries its context parameter as an
	// ordinary slot here exactly as the top-level accessor path does, so the accessor's arity matches its call sites'.
	// The `mods.context` marker rides with it for the cross-module restore ([KotlinContextParameter]). Kept as this
	// explicit {name,type} projection rather than routed through [paramsJsonList]: that helper would ALSO start
	// emitting parameter ANNOTATIONS on a member accessor (a `@setparam:` on a setter's `value`), which this
	// declaration has never carried — a change of subject, and one no test pins.
	val ps = (listOfNotNull(selfParam) + acc.parameters.filter { isValueParameter(it) }
		.map {
			val ctxMod = if (it.kind == IrParameterKind.Context) ""","mods":{"context":true}""" else ""
			// A setter's `value` slot can itself BE a context function type (`var p: context(A) () -> Unit`); its arity
			// rides here exactly as it does on an ordinary parameter, via the property fallback in [ctxFnCountFor].
			val ctxFn = ctxFnTypeField(ctxFnCountFor(it))
			"""{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}$ctxFn$ctxMod}"""
		}).joinToString(",")
	val isAbstract = acc.modality == Modality.ABSTRACT && acc.body == null
	// #6 non-null parameter PRECONDITIONS (a setter's `value` param) at entry + a getter's non-null return POSTCONDITION
	// (a setter returns Unit -> naturally out of scope). An abstract accessor declares only a slot: it has no entry at
	// which a precondition can run, and its BIR body must remain empty all the way to ilemit.
	val bodyStmts = if (isAbstract) "" else withReturnPostcondition(acc) {
		(acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	}
	val body = if (isAbstract) "" else
		(preconditionChecks(acc) + listOfNotNull(bodyStmts.takeIf { it.isNotEmpty() })).joinToString(",")
	activeDelegatedAccessor = savedDelegatedAccessor
	activeSemanticOwner = savedSemanticOwner
	if (extRecv != null) selfSubst.remove(extRecv)
	val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
	// An `override val/var` whose accessor overrides a base CLASS/ENUM_CLASS accessor must REUSE that base virtual
	// slot (`override`, not a fresh NewSlot) — EXACTLY like an overriding method (see method()'s `isOverride`).
	// Otherwise a concrete subclass leaves the base's abstract accessor slot unfilled -> TypeLoadException at load
	// ("get_X ... does not have an implementation"). This mirrors method() so property accessors and methods agree.
	// Interface members bind by name/signature (ilemit's DefineMethodOverride pass) so they don't need this flag;
	// use the accessor's OWN overriddenSymbols (a setter that ADDS to a base `val` has none -> stays a NewSlot).
	// A `companion { }` property's accessors carry the property's static fact: fir2ir omits their dispatch receiver
	// exactly as it does for a companion-block function. A static member occupies no virtual slot, so it can be
	// neither virtual nor an override — the frontend cannot produce one, and the flags are pinned here so the two
	// facts cannot disagree.
	val isStatic = acc.isStaticMethodOfClass
	val isOverrideClass = !isStatic && acc.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
	val virtual = !isStatic && (acc.modality == Modality.OPEN || acc.modality == Modality.ABSTRACT || acc.overriddenSymbols.isNotEmpty())
	val vis = visOf(acc)
	// Emit the PROPERTY's annotations (e.g. @ClrIntrinsic) onto its accessor method — the SAME unconditional
	// pass-through method()/ifaceMethod already do for plain methods (kotc does not filter/select annotations;
	// attrsJson doctrine). The @ClrIntrinsic is on the property (`@ClrIntrinsic("Length") val length`), so read it
	// from the corresponding property. bir2cir consumes it from the explicitly associated accessor (TryMemberIntrinsic /
	// DeclarationRename) to lower a `.length` read to clrPropGet Length. In a stdlib build the ref.dll carries the
	// binding; the rt build strips ALL metadata downstream (ilemit under `--build-stdlib=runtime`) so the rt.dll
	// never carries it. In an app build these attrs simply ride the accessor as ordinary metadata.
	val propAnns = (acc.correspondingPropertySymbol?.owner ?: acc).annotations
	val accAttrs = ""","attrs":[${attrsJson(propAnns)}]"""
	val kotlinStatic = if (isStatic && acc.correspondingPropertySymbol?.owner?.let { isKotlinStaticProperty(it) } == true)
		""","kotlinStatic":true""" else ""
	val property = acc.correspondingPropertySymbol?.owner
		?: error("accessor '$propName' has no corresponding property")
	return """{"name":${str(propName)}${declarationIdField(acc)}${explicitClrNameField(acc)}${propertyAccessorFact(property, kind)},"static":$isStatic$kotlinStatic,"override":$isOverrideClass,"virtual":$virtual,"abstract":$isAbstract,"objectOverride":false,"vis":${str(vis)}${typeParamsJson(acc.typeParameters)},"params":[$ps],"ret":${str(ret)}${retCtxFnTypeField(acc)}${funModsJson(acc)},"body":[$body]$accAttrs${overridesJson(acc)}}"""
}

/** A user `annotation class Ann(val v: Int, …)` -> a plain BIR class carrying the pure-Kotlin `"annotation":true`
 *  FLAG (ctor params -> public fields). "This is an annotation" is a Kotlin-language fact; "annotations extend
 *  System.Attribute on the CLR" is the Kotlin<->CLR relation, so kotc emits ONLY the flag (base:null) and
 *  bir2cir DERIVES `base = System.Attribute` from it (annotation-base-lowering-to-bir2cir, USER 2026-07-02).
 *  kotc names NO CLR base type here. */
internal fun BirEmitter.annotationDef(klass: IrClass): String {
	rejectClrEnumOnNonEnum(klass)
	val ctorParams = klass.declarations.filterIsInstance<IrConstructor>().firstOrNull { it.isPrimary }
		?.parameters?.filter { it.kind == IrParameterKind.Regular }.orEmpty()
	val fields = ctorParams.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
	val assigns = ctorParams.joinToString(",") { """{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(it.name.asString())},"value":{"k":"local","name":${str(it.name.asString())}}}""" }
	val ctor = """{"params":[$fields],"baseArgs":[],"thisArgs":null,"vis":"public","body":[$assigns]}"""
	return """{"name":${str(typeName(klass))},"kind":"class"${semanticOwnerJson(klass)}${classModsJson(annotation = true)},"abstract":false,"vis":"public","base":null,"interfaces":[],"fields":[$fields],"ctors":[$ctor],"methods":[]}"""
}

/** The `attrs` JSON for a declaration: each annotation -> a .NET custom attribute application. The `attr` type is a
 *  structured `{t:fqn}` identity node (#48). A Kotlin-authored annotation is named by its plain Kotlin FQN (#46) —
 *  bir2cir derives its `: System.Attribute` base from the `"annotation":true` flag on the class def. An imported .NET
 *  attribute (a dll2klib-projected annotation class) is named by its real .NET FQN and flagged `"attrClr":true` (a
 *  frontend origin fact carried by KLIB, via clrName); bir2cir consumes that flag into the
 *  `attrExternal` bit so ilemit binds the existing .NET constructor (#54/#48). kotc emits no `clr:` marker.
 *
 *  kotc does NOT filter/select annotations: from kotc's view an annotation is just METADATA, so EVERY annotation is
 *  passed through to the BIR verbatim (incl. @ClrTypeAlias, @ClrIntrinsic, and every other `kotlin.*` annotation).
 *  The ref.dll consumer (bir2cir) is the CLR layer that decides what to do with each attribute. (The old keep-list —
 *  drop `kotlin.*` except @ClrIntrinsic — was a kotc-side SELECT and is removed: a
 *  metadata-selection policy must NOT live in kotc.) If emitting some Kotlin-internal annotation type breaks
 *  downstream (its `: System.Attribute` type or an arg type being unresolvable at ilemit), that is a bir2cir/ilemit
 *  concern, NOT a reason to re-introduce a kotc filter. */
internal fun BirEmitter.attrsJson(anns: List<IrConstructorCall>): String {
	// kotc emits ONE BIR for every build: annotations ride EVERY build's BIR verbatim (ref/app/rt identical here).
	// The rt-build strip is downstream in bir2cir (RoundtripMetadata.StripRuntimeAttrs) and is SELECTIVE — it drops
	// the compile-time-only carriers (`DotKt.Runtime.CompilerServices.*` round-trip attrs / `kotlin.clr.*` @Clr binding
	// / NRT) but KEEPS the user's own annotations (kotlin.Deprecated / SinceKotlin / …) on the shipping rt.dll (#47).
	return anns.mapNotNull { ann ->
		val ac = ann.symbol.owner.parent as? IrClass ?: return@mapNotNull null
		if (ac.kind != ClassKind.ANNOTATION_CLASS) return@mapNotNull null
		val clr = clrName(ac)
		val attrClr = if (clr != null) ""","attrClr":true""" else ""
		val args = regularArgs(ann)
		"""{"attr":${fqnJson(clr ?: typeName(ac))}$attrClr,"argTypes":[${args.joinToString(",") { birType(it.type).toJson() }}],"args":[${args.joinToString(",") { expr(it) }}]}"""
	}.joinToString(",")
}

internal fun BirEmitter.typeDef(klass: IrClass, captures: List<Pair<IrValueDeclaration, String>> = emptyList(), isObject: Boolean = false, captureEnclosingGenerics: Boolean = false, generated: Boolean = false): String {
	rejectClrEnumOnNonEnum(klass)
	val baseType = klass.superTypes
		.firstOrNull { val k = it.classifierOrNull?.owner as? IrClass; k != null && k.kind == ClassKind.CLASS && k.fqNameWhenAvailable?.asString() != "kotlin.Any" }
	val base = baseType?.classifierOrNull?.owner as? IrClass
	// A LIFTED anonymous/local class that CAPTURES enclosing generic type parameters (reified CLR generics —
	// `object : Box<T>`, an inlined `object` whose supertype/captures resolve to the enclosing `T`, or a function-local
	// `class L { val x: T = t }`) must be GENERIC over them itself: on the CLR a `tv` referenced by its members is
	// unresolved unless the flattened class DECLARES the param and the construction site instantiates it with the
	// enclosing arg (mirrors newClosure/newSam). This runs ONLY for the LIFTED object-literal / local-class paths
	// (`captureEnclosingGenerics`) — a normal top-level/nested named declaration owns all of its params — and derives the
	// captured set STRUCTURALLY from the class's real type positions (supertypes, own type-param bounds, captured-var
	// field types, ctor/member parameter + return + body-operand types). It deliberately does NOT scan a member's CALL
	// nodes: a `tv` inside a call's `sig` metadata is the CALLEE's own param (e.g. `clrCollAddAll<T>`), NOT an enclosing
	// capture — that over-captured, giving a normal `ArrayList<E>` a spurious `T` (arity-2, rt break).
	//
	// CRITICAL (the flip): the captured param `T` is declared on the ENCLOSING function/type, so birType renders every
	// member use of it as a scope="method"/"type" `tv` of that ENCLOSING owner — which is unresolvable once the class is
	// flattened to a standalone generic type. So the scan+install runs BEFORE the members are rendered, and installs a
	// typeArgSubst remapping each captured param onto THIS class's own generic space (scope="type", the flattened index
	// AFTER the class's own params). Rendering members then honors the remap → resolvable `{tv,type,i}`; restored at end.
	val ownTps = innerEnclosingTypeParams(klass) + klass.typeParameters
	val ownNames = ownTps.map { it.name.asString() }.toHashSet()
	val capturedTpParams = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	// A lifted implementation type retains its lexical class owner as a Kotlin semantic fact. Record the complete
	// owner-generic correspondence even when this particular body does not otherwise mention every owner parameter:
	// bir2cir may choose CLR nesting, where a call moved back onto that owner still needs a valid constructed owner.
	// The parameters remain after the implementation type's own parameters in BIR; outerTypeParamOffset below tells
	// bir2cir where the semantic owner segment begins, without kotc choosing CLR generic-slot order.
	fun lexicalClassOwner(): IrClass? {
		var parent: Any? = klass.parent
		while (parent != null) {
			when (parent) {
				is IrClass -> return if (parent.isCompanion) parent.parent as? IrClass else parent
				is IrDeclaration -> parent = parent.parent
				else -> return null
			}
		}
		return null
	}
	val staticSemanticOwner = if (captureEnclosingGenerics) staticSemanticTypeOwner(klass) else null
	val lexicalOwner = if (captureEnclosingGenerics) lexicalClassOwner() else null
	val staticOwnerTps = if (staticSemanticOwner != null && lexicalOwner != null)
		(innerEnclosingTypeParams(lexicalOwner) + lexicalOwner.typeParameters +
			liftedTypeArgParams[lexicalOwner].orEmpty()).toSet()
	else emptySet()
	val liftedOwnerTps = if (captureEnclosingGenerics && staticSemanticOwner == null) lexicalOwner?.let { owner ->
		(innerEnclosingTypeParams(owner) + owner.typeParameters + liftedTypeArgParams[owner].orEmpty()).distinct()
	}.orEmpty() else emptyList()
	if (captureEnclosingGenerics) {
		capturedTpParams.addAll(liftedOwnerTps)
		fun scan(t: IrType, excluded: Set<String>) {
			val cls = t.classifierOrNull
			if (cls is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol) {
				// Capture a param that is (a) NOT inline-substituted, OR (b) substituted to an unresolved ENCLOSING
				// type var. Case (b) is the object literal produced by INLINING a generic fn: `Sequence<T>{...}`
				// inlined into `asSequence<T>` maps the callee's `T` to the CALLER's method-scoped `tv` in
				// typeArgSubst, which cannot resolve once the anon is flattened to a standalone generic class
				// (ilemit falls the unresolvable `!!method` to `object` -> `IEnumerable<object>` vs the real
				// value-type `Iterator<int>` -> EntryPointNotFound). So a `tv`-valued subst still needs the param
				// declared on THIS class + instantiated at the `new` site; a subst to a CONCRETE type resolves fine.
				val subst = typeArgSubst[cls.owner]
				if ((subst == null || containsTv(subst)) && cls.owner !in staticOwnerTps &&
					cls.owner.name.asString() !in excluded)
					capturedTpParams.add(cls.owner)
				return
			}
			(t as? IrSimpleType)?.arguments?.forEach { (it as? IrTypeProjection)?.type?.let { at -> scan(at, excluded) } }
		}
		// Supertypes, own type-param bounds, and captured-var field types can only reference ENCLOSING params.
		klass.superTypes.forEach { scan(it, ownNames) }
		klass.typeParameters.forEach { tp -> tp.superTypes.forEach { scan(it, ownNames) } }
		captures.forEach { scan(it.first.type, ownNames) }
		klass.declarations.forEach { d ->
			when (d) {
				// A member/ctor may ALSO reference an enclosing param in its signature or a reified body operand (`is R`);
				// exclude that member's OWN type params (a generic method's `<U>` is not a class capture).
				is IrSimpleFunction -> {
					val excl = ownNames + d.typeParameters.map { it.name.asString() }
					d.parameters.forEach { scan(it.type, excl) }
					scan(d.returnType, excl)
					bodyTypeOperands(d).forEach { scan(it, excl) }
				}
				is IrConstructor -> {
					d.parameters.forEach { scan(it.type, ownNames) }
					bodyTypeOperands(d).forEach { scan(it, ownNames) }
				}
				is IrProperty -> {
					d.backingField?.let { scan(it.type, ownNames) }
					d.getter?.let { scan(it.returnType, ownNames) }
				}
				else -> {}
			}
		}
		// A captured param's own BOUND can name a FURTHER enclosing param (`<T, U> where T : Box<U>`): this class
		// re-declares the bound along with the param, so it must re-declare that one too or the constraint references a
		// variable the flattened class does not have. Close the set (worklist — a bound can pull in a param whose own
		// bound pulls in another; `capturedTpParams` is a set, so a cyclic bound terminates).
		val pending = ArrayDeque(capturedTpParams)
		while (pending.isNotEmpty()) {
			val before = capturedTpParams.size
			pending.removeFirst().superTypes.forEach { scan(it, ownNames) }
			if (capturedTpParams.size != before) pending.addAll(capturedTpParams.drop(before))
		}
	}
	// Install the enclosing→own-generic-space remap for the captured params BEFORE any member is rendered.
	val savedCaptureSubst = HashMap<org.jetbrains.kotlin.ir.declarations.IrTypeParameter, TypeNode?>()
	capturedTpParams.forEachIndexed { i, tp ->
		savedCaptureSubst[tp] = typeArgSubst[tp]
		typeArgSubst[tp] = TypeNode.Tv("type", ownTps.size + i)
	}
	liftedTypeArgParams[klass] = capturedTpParams.toList()
	liftedTypeArgNames[klass] = capturedTpParams.map { it.name.asString() }
	// A companion reaches BIR as its own representation-neutral semantic type. Its declaration visibility belongs
	// to that type; each member retains its own Kotlin visibility. The CLR nested TypeDef therefore gates the whole
	// companion without incorrectly turning a source-public member into a Family member of the carrier itself.
	// #187: a class DIRECTLY implementing a .NET interface event must `override val E by clrEvent()` (else invalid type).
	checkUnimplementedClrEvents(klass)
	// `override val E by clrEvent()` synthesis (§4.2): the field-like event impl (backing directive + add_/remove_/raise_).
	val (clrEventBackings, clrEventMethods) = synthClrEvents(klass)
	// `class A : B by c` over a CLR event synthesizes forwarding add_/remove_ accessors (no backing field, no raise).
	val (clrEventForwarders, clrEventForwardMethods) = synthClrEventForwarders(klass)
	val instStorageFields = instanceStorageFields(klass)
	val staticPropFields = staticPropertyFields(klass)
	// A capturing object literal carries its captured outer values as extra instance fields.
	val capFields = captures.map { (decl, fname) ->
		"""{"name":${str(fname)},"type":${str(captureFieldType(decl))},"vis":"private"}"""
	}
	// `object` singleton: a static `INSTANCE` field initialized to `new Foo()` (run in the .cctor) — same shape
	// as an enum entry. `IrGetObjectValue` loads it; member access then routes as normal instance access.
	val instanceField = if (isObject && !klass.isCompanion)
		listOf("""{"name":"INSTANCE","type":${fqnJson(typeName(klass))},"static":true,"init":{"k":"new","type":${fqnJson(typeName(klass))},"argTypes":[],"args":[]}}""")
	else emptyList()
	val fields = (instStorageFields + staticPropFields + capFields + instanceField).joinToString(",")
	val ctors = klass.declarations.filterIsInstance<IrConstructor>().joinToString(",") { ctor(klass, it, captures) }
	// `companion { fun f() }` is a static member of THIS type; every other member is an instance one. The partition
	// is the frontend's static fact ([isKotlinStaticFunction]), not a shape guess.
	val instMethods = klass.declarations.filterIsInstance<IrSimpleFunction>()
		// Include `abstract fun`s (body == null): they emit as CLR abstract methods so subclass overrides bind
		// and a base-typed call (`shape.area()`) resolves to the slot.
		.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && (it.body != null || it.modality == Modality.ABSTRACT) }
		// The method twin of `inheritedStatic` below: a static of a BASE type has nothing to emit here.
		.filter { !isInheritedStaticFunction(it) }
		.map { method(it, static = isKotlinStaticFunction(it)) }
	// User custom and frontend-generated delegated accessors become explicit accessor declarations.
	// A property optimizes to a plain field; but one implementing a Kotlin interface property must emit an accessor
	// METHOD to bind the interface slot (property-accessor analog of the method-side overridesIface fix; e.g.
	// ComparableRange.start over ClosedRange.start). See design-clr-property-model.md. This is ALSO the sole producer
	// of accessors that OVERRIDE a .NET base-CLASS virtual property (`override val Message` over System.Exception):
	// accessorMethod emits the source property identity/role plus the `overrides` marker; bir2cir derives the
	// `clrOverride` field from that marker (kotc emits no clrOverride — A2 / #73 M4.3).
	fun ovIface(a: IrSimpleFunction) = a.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.INTERFACE }
	// A FAKE-OVERRIDE property whose implementation is INHERITED FROM A BASE CLASS (`name` in `Sq : Shape("sq")`)
	// or from a CLR default interface property has accessors with NO body. Emitting such an accessor creates a new,
	// empty override (returning the CLR default value) and shadows the inherited implementation. An ABSTRACT
	// fake-override resolved only to an INTERFACE member (AbstractMutableList.size over MutableList.size) is KEPT:
	// the CLR requires the (abstract) class to re-declare the unimplemented interface slot.
	fun implementationInherited(a: IrSimpleFunction?): Boolean {
		val resolved = a?.let(::selectedInheritedImplementation) ?: return false
		return (resolved.parent as? IrClass)?.kind == ClassKind.CLASS || resolved.modality != Modality.ABSTRACT
	}
	fun dropFake(a: IrSimpleFunction?) = a?.let(::isInheritedSynthetic) == true && implementationInherited(a)
	fun inheritedStatic(p: IrProperty) = isInheritedStaticProperty(p)
	// `!isClrEventProperty`: a `kotlin.clr.ClrEvent<T>` fake-override (a .NET event inherited through a base's
	// interface) is not a real property and must not surface an accessor/property member.
	fun emitsGet(p: IrProperty) = p.getter != null && !p.isConst && !p.isLateinit && !isClrField(p) && !dropFake(p.getter) && !isClrEventProperty(p) && !inheritedStatic(p)
	fun emitsSet(p: IrProperty) = p.setter != null && !p.isConst && !p.isLateinit && !isClrField(p) && !dropFake(p.setter) && !isClrEventProperty(p) && !inheritedStatic(p)
	val inheritedDefaultAccessors = klass.declarations.filterIsInstance<IrProperty>().flatMap { p ->
		listOfNotNull(
			inheritedDefaultAccessorFact(p, p.getter, "get"),
			inheritedDefaultAccessorFact(p, p.setter, "set"))
	}
	val inheritedDefaultMethods = klass.declarations.filterIsInstance<IrSimpleFunction>()
		.mapNotNull(::inheritedDefaultMethodFact)
	val userAccessors = klass.declarations.filterIsInstance<IrProperty>().flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	// Real CLR properties: a `properties` entry per accessor-bearing property -> ilemit DefineProperty's it over
	// the associated accessor methods, so a C#/reflection consumer sees a property. (Full "every property -> CLR property +
	// @ClrField opt-out" is the next phase; field-backed props keep their backing field for now.)
	val propsList = klass.declarations.filterIsInstance<IrProperty>().filter { emitsGet(it) }.joinToString(",") { p ->
		val kotlinStatic = if (isKotlinStaticProperty(p)) ""","kotlinStatic":true""" else ""
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()}${kotlinPropertyAccessors(p, emitsSet(p))}$kotlinStatic${overridesJson(p.getter!!)}}"""
	}
	val methods = (instMethods + userAccessors + clrEventMethods + clrEventForwardMethods).joinToString(",")
	// A .NET base class (`: System.Exception(...)`, incl. a generic `: Collection<Int>()`) -> a `clr:`/`clrg:`
	// type spec (via birType) that ilemit resolves by reflection; a Kotlin-user base emits its bare FQN identity
	// carrying its ACTUAL constructed type arguments.
	val baseJson = base?.let {
		// A projected .NET base carries its full constructed identity (birType). A Kotlin-user/stdlib base emits its
		// IDENTITY via `ownerSpec` — the base supertype's OWN resolved type arguments: the subclass's type params as
		// `tv` when the base is over them (`ArrayList<E> : AbstractList<E>` -> `AbstractList<tv E>`), the enclosing
		// args for an inner-class base, AND CONCRETE types when the subclass supplies them (a non-generic `object Key :
		// AbstractCoroutineContextKey<ContinuationInterceptor, CoroutineDispatcher>` -> the base carries both concrete
		// args). ilemit constructs the generic base via `MakeGenericType` — a positional-params-only emission would
		// SILENTLY DROP concrete base args (an external generic stdlib base then failed to resolve by bare name).
		// BaseName stays the bare FQN (SlotName reads `.name`) so the base-chain walk still keys by bare name.
		// (bir2cir substitutes stdlib bases.)
		if (isExternalNetType(it)) birType(baseType!!).toJson()
		else ownerSpec(it, baseType).toJson()
	} ?: "null"
	// Stdlib interface supertypes (Iterator, Iterable, Read(Write)Property) -> the REAL generic identity;
	// a user generic interface `Container<Int>` -> the constructed spec `Container[int]` (ownerSpec).
	val ifaces = interfaceSuperTypes(klass)
	// Anonymous objects are synthetic implementation types and remain public. Lifted companions also use anonNames
	// for their physical name, but their source visibility is authoritative: widening a private companion here makes
	// its carrier an ordinary public CLR/KLIB type on re-import.
	val vis = if (anonNames.containsKey(klass) && !klass.isCompanion) "public" else visOf(klass)
	val isAbstract = klass.modality == Modality.ABSTRACT || klass.modality == Modality.SEALED
	// Preserve Kotlin ownership only. bir2cir decides how this semantic child is represented in CLR metadata.
	val semanticOwner = semanticOwnerJson(klass)
	val staticSemanticOwnerFact = staticSemanticOwner?.let { ""","staticSemanticOwner":${str(it)}""" }.orEmpty()
	val outerTypeParamCount = if (captureEnclosingGenerics) liftedOwnerTps.size else innerEnclosingTypeParams(klass).size
	val outerTypeParamsFact = if (outerTypeParamCount == 0) "" else
		""","outerTypeParamCount":$outerTypeParamCount"""
	val outerTypeParamOffsetFact = if (outerTypeParamCount == 0 || !captureEnclosingGenerics) "" else
		""","outerTypeParamOffset":${ownTps.size}"""
	// Round-trip: a Kotlin `sealed` class lowers to a CLR abstract class (loses the sealed modality) — carry the fact
	// so a re-consuming Kotlin module restores `sealed` (ilemit stamps [KotlinSealed]). `value` (inline class) is
	// likewise carried as a mod — the 2.4.0 frontend no longer surfaces its `@JvmInline` annotation, so this modifier
	// is bir2cir's sole value-class signal for the erase-to-underlying lowering (see classModsJson).
	val sealedFlag = classModsJson(
		sealed = klass.modality == Modality.SEALED,
		value = klass.isValue,
		objectSingleton = isObject && !klass.isCompanion,
		inner = klass.isInner
	)
	// typeParams = the anon/class's own params PLUS the captured enclosing params (scanned + installed at the top).
	// The captured ones go through the SAME renderer as the class's own, so they keep their BOUNDS: emitting them as
	// bare names dropped every constraint, and a member that needs one (`v.get()` on a `T : Box<U>` capture) then has no
	// constraint to dispatch through — a silently wrong signature, not a diagnostic. Their bounds render through the
	// typeArgSubst installed above, so a bound naming another captured param resolves in THIS class's space.
	val ownTpsJson = typeParamsJson(ownTps).removePrefix(""","typeParams":[""").removeSuffix("]")
	val extraJson = typeParamsJson(capturedTpParams.toList())
		.removePrefix(""","typeParams":[""").removeSuffix("]")
	val tpEntries = listOf(ownTpsJson, extraJson).filter { it.isNotEmpty() }.joinToString(",")
	val tpJson = if (tpEntries.isEmpty()) "" else ""","typeParams":[$tpEntries]"""
	// #68: a compiler-generated synthetic (a lifted anon-object / local class) carries `generated:true` — a STRUCTURAL
	// fact (no `<>` CLR-unspeakability marker; that is ilemit's concern). ilemit reads it to stamp [CompilerGenerated].
	val generatedFlag = if (generated) ""","generated":true""" else ""
	// #275: preserve the KOTLIN companion association independently of its CLR representation.
	// bir2cir owns turning this semantic fact into the physical round-trip metadata carrier. In particular, no consumer
	// may infer either the association or the source name from the `<Name>CompanionObject` CLR spelling.
	val kotlinCompanion = when {
		klass.isCompanion -> {
			val outer = (klass.parent as? IrClass)
				?: error("companion '${klass.name}' has no Kotlin class owner")
			kotlinCompanionFact(outer, klass)
		}
		else -> ""
	}
	// `clrEvents` (§4.2): per-event backing directives for the `by clrEvent()` synthesis — bir2cir's ClrEventImplBinding
	// turns each into a real `<E>$delegate : D` field + a type-level `clrEventDecl` (the `.event` metadata record).
	val clrEventsJson = if (clrEventBackings.isEmpty()) "" else ""","clrEvents":[${clrEventBackings.joinToString(",")}]"""
	val clrEventForwardersJson = if (clrEventForwarders.isEmpty()) "" else
		""","clrEventForwarders":[${clrEventForwarders.joinToString(",")}]"""
	val inheritedDefaultsJson = if (inheritedDefaultAccessors.isEmpty()) "" else
		""","inheritedDefaultAccessors":[${inheritedDefaultAccessors.joinToString(",")}]"""
	val inheritedDefaultMethodsJson = if (inheritedDefaultMethods.isEmpty()) "" else
		""","inheritedDefaultMethods":[${inheritedDefaultMethods.joinToString(",")}]"""
	val result = """{"name":${str(typeName(klass))},"kind":"class","abstract":$isAbstract,"vis":${str(vis)}$semanticOwner$staticSemanticOwnerFact$outerTypeParamsFact$outerTypeParamOffsetFact$sealedFlag$tpJson$generatedFlag$kotlinCompanion,"base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods],"properties":[$propsList]$inheritedDefaultsJson$inheritedDefaultMethodsJson$clrEventsJson$clrEventForwardersJson,"attrs":[${attrsJson(klass.annotations)}]${posJson(klass)}}"""
	// Restore the captured-param remap installed at the top.
	savedCaptureSubst.forEach { (tp, prev) -> if (prev != null) typeArgSubst[tp] = prev else typeArgSubst.remove(tp) }
	return result
}

internal fun BirEmitter.ctor(klass: IrClass, ctor: IrConstructor, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
	val savedSemanticOwner = activeSemanticOwner
	activeSemanticOwner = if (klass.isCompanion)
		(klass.parent as? IrClass)?.let(::typeName) ?: fileClass
	else typeName(klass)
	// Captured outer values arrive as leading ctor params and are stored into the capture fields first
	// (the instance initializers below read them, e.g. `var cur = from` -> `this.__outer.from`).
	val capParams = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	val capAssigns = captures.map { (_, fname) ->
		"""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}"""
	}
	// `ctor` as the owner so its defaulted params carry `@KotlinDefault` (the cross-module splice source), exactly as a
	// carrying function's do — a re-consumed constructor's non-constant default has no other carrier. NOT for a lifted
	// LOCAL/anonymous class: its `capParams` are extra leading args the stamped index does not count (only an inner
	// class's single `__outer` lines up, via `extOffset`), and its default expression reads those captures as
	// `this.<field>` — an unbindable carrier. A local class has no cross-module call site to carry anything for.
	val carrierOwner = ctor.takeIf { captures.isEmpty() || klass.isInner }
	val params = (capParams + paramsJsonList(ctor.parameters, carrierOwner)).joinToString(",")
	val body = ctor.body as? IrBlockBody
	val delegating = body?.statements?.filterIsInstance<IrDelegatingConstructorCall>()?.firstOrNull()
	val delegateClass = delegating?.symbol?.owner?.parent as? IrClass
	// `constructor(...) : this(...)` delegates to a sibling ctor; `: super(...)` / implicit -> base.
	val isThisDelegate = delegating != null && delegateClass == klass
	// A delegation is an ordinary omitting call site (`: this(3)` on `class D(val a: Int, val b: Int = a * 2)`), so its
	// args run through the SAME default-filling pass every other call shape uses — dropping the omitted slot instead
	// would slide every later arg into the wrong parameter.
	//   The delegation is emitted BEFORE the ctor body, where the capture FIELDS are not yet stored (capAssigns above
	// run as the body's first statements): while emitting its args, every captured value must read the leading capture
	// PARAM instead of `this.<field>` — the enclosing instance of an inner class included, whose class-body binding is
	// the `__outer` FIELD. A lifted local class's captures are also leading params of the TARGET sibling ctor, so a
	// `: this(...)` passes them along (an inner class's enclosing instance is the delegation's dispatch-receiver arg
	// instead, prepended by `delegatedCtorArgs`).
	val savedCapSubst = java.util.IdentityHashMap<IrValueDeclaration, String?>()
	captures.forEach { (d, fname) ->
		savedCapSubst[d] = captureSubst[d]
		captureSubst[d] = """{"k":"local","name":${str(fname)}}"""
	}
	val capForwardArgs = if (klass.isInner) emptyList() else captures.map { (_, f) -> """{"k":"local","name":${str(f)}}""" }
	// A delegation is an ordinary call site with its own EVALUATION PLAN (§2.7), but it is not an expression — it rides
	// the ctor declaration, ahead of the body — so there is no wrapping `valueBlock` to lower the plan into. The
	// bindings ride the declaration instead, as `delegationBindings`; bir2cir's CallEvalLowering turns them into
	// `preStmts`, which ilemit emits before the `this`/`base` call itself. At most one delegation exists per
	// constructor, so the two arms below share the one slot.
	var delegationBindings: String? = null
	val thisArgs = if (isThisDelegate) withCallPlan(delegating!!) {
		capForwardArgs + delegatedCtorArgs(delegating)
	}.let { (plan, a) ->
		delegationBindings = plan.bindingsJson().takeIf { !plan.isEmpty }
		a.joinToString(",")
	} else null
	val baseArgs = if (!isThisDelegate) delegating?.let { d ->
		val targetFq = delegateClass?.fqNameWhenAvailable?.asString()
		if (targetFq != "kotlin.Any") {
			// A CAPTURING local base took its captures as hidden leading ctor parameters when lifted. The derived
			// local class's transitive capture scan already added the same declarations to this ctor, so forward THIS
			// ctor's capture parameters before the base's source-level/default-filled arguments. The capture fields
			// are assigned only after the base call and cannot be read here.
			val baseCaptureArgs = delegateClass?.let { localClassCaptures[it] }.orEmpty().map { baseCapture ->
				val here = captures.firstOrNull { (own, _) -> own === baseCapture }?.second
					?: return@let invariantBroken(delegating,
						"a local base class's capture is not a capture of the derived local class")
				"""{"k":"local","name":${str(here)}}"""
			}
			withCallPlan(d) { baseCaptureArgs + delegatedCtorArgs(d) }
				.let { (plan, a) ->
					delegationBindings = plan.bindingsJson().takeIf { !plan.isEmpty }
					a.joinToString(",")
				}
		}
		else null
	} else null
	savedCapSubst.forEach { (d, prev) -> if (prev != null) captureSubst[d] = prev else captureSubst.remove(d) }
	val stmts = ArrayList<String>()
	stmts.addAll(capAssigns)   // store captures before instance initializers, which may read them
	body?.statements?.forEach { s ->
		when (s) {
			is IrDelegatingConstructorCall -> {}
			is IrInstanceInitializerCall -> stmts.addAll(instanceInitializerStatements(klass))
			else -> stmts.add(stmt(s))
		}
	}
	val baseJson = baseArgs?.let { "[$it]" } ?: "null"
	val thisJson = thisArgs?.let { "[$it]" } ?: "null"
	// Constructor delegation carries the frontend-selected declaration signature just like an ordinary call's `sig`.
	// The target emitter may LINK this exact identity, but must not choose among same-arity CLR constructors from the
	// reflection enumeration order. Include every physical leading slot authored here: captures forwarded to a lifted
	// local target, or the enclosing-instance receiver of an inner target.
	val hiddenDelegationSig = when {
		delegating == null -> emptyList()
		isThisDelegate && !klass.isInner -> captures.map { (decl, _) -> str(captureFieldType(decl)) }
		!isThisDelegate -> delegateClass?.let { localClassCaptures[it] }.orEmpty()
			.map { str(captureFieldType(it)) }
		else -> emptyList()
	}
	val delegationSig = delegating?.let { d ->
		val enclosing = listOfNotNull(dispatchReceiver(d)?.let { birType(it.type).toJson() })
		val declared = d.symbol.owner.parameters.filter { it.kind == IrParameterKind.Regular }
			.map { birType(it.type).toJson() }
		(hiddenDelegationSig + enclosing + declared).joinToString(",")
	}
		?.let { ""","delegationSig":[$it]""" } ?: ""
	// #6 non-null parameter PRECONDITIONS at entry. They land AFTER the base/`this` ctor delegation (baseArgs/thisArgs
	// ride a separate field), so a null user param dereferenced by a base-ctor arg NREs before this friendly NPE — an
	// accepted ordering deviation from JVM's before-super() insertion (docs/dotkt-semantics.md).
	val ctorBody = (preconditionChecks(ctor) + stmts).joinToString(",")
	activeSemanticOwner = savedSemanticOwner
	val bindingsJson = delegationBindings?.let { ""","delegationBindings":$it""" } ?: ""
	return """{"params":[$params],"baseArgs":$baseJson,"thisArgs":$thisJson$delegationSig$bindingsJson,"vis":${str(visOf(ctor))},"body":[$ctorBody]}"""
}

internal fun BirEmitter.method(fn: IrSimpleFunction, static: Boolean, semanticOwnerOverride: String? = null): String {
	val savedSemanticOwner = activeSemanticOwner
	// A synthesized BIR declaration can have a different identity from the FIR class that supplied its members. Rich
	// enum entry bodies are the concrete case: FIR calls the class `E.ENTRY`, while BIR explicitly declares the entry
	// subclass as `<>E_ENTRY`. The body must own further declarations under the BIR declaration being emitted; this is
	// still a Kotlin/BIR ownership fact, not a CLR placement decision.
	activeSemanticOwner = semanticOwnerOverride ?: semanticOwnerName(fn)
	// An override of a CLASS or ENUM_CLASS member (the latter: a per-entry enum body overriding an abstract enum
	// member) reuses the base virtual slot. (Interface members bind by name/signature, handled elsewhere.)
	// A static member occupies no virtual slot: it cannot override and cannot be overridden. Kotlin still reports the
	// default modality of the enclosing declaration (an interface member is `open` unless written otherwise), so pin
	// both flags off `static` here rather than letting a default modality contradict the static fact.
	val isOverride = !static && fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
	// A method that implements/overrides a Kotlin INTERFACE member must be virtual on the CLR to bind the interface
	// slot — even when it is Kotlin-`final` (final override -> CLR `virtual final` = sealed). Otherwise the type
	// fails to load with "must be virtual to implement a method on an interface or super type" (e.g. Enum.compareTo,
	// the primitive Iterator.next).
	val overridesIface = !static && fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.INTERFACE }
	val isVirtual = !static && (fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT || overridesIface)
	// An extension function `fun T.f()` -> static method whose first param `__self` is the receiver;
	// body references to the receiver resolve to `__self` (via valSubst).
	val extRecv = extensionReceiverParam(fn)
	if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
	// (No fun-interface SAM param-erasure here: kotc reads NEITHER @ClrTypeAlias NOR @ClrIntrinsic — foundational
	// invariant.) A fun interface aliased to a NON-generic BCL interface would be derived in bir2cir off the ref.dll;
	// but the stdlib no longer aliases any `fun interface` to a BCL interface — Comparator is a plain Kotlin fun
	// interface (see ComparatorClr.kt: the old @ClrIntrinsic("System.Collections.IComparer") erasure was a misdiagnosis,
	// fixed at the ilemit `unbox.any` source), so no object-erasure bridge is needed on the Kotlin side.
	// `tailrec` tail-call optimization (§2b): if this is a `tailrec` fn with an actual self-tail-call, install a
	// TailrecCtx so each tail call emits a back-jump (tailrecJump) instead of recursing, and prefix the body with
	// the entry label the jumps target. The frontend already validated the tail positions; we reuse its own
	// collectTailRecursionCalls. Restored after the body so a nested/sibling fn is unaffected.
	val savedTailrec = tailrecCtx
	val tailrecStart: Int? = if (fn.isTailrec) {
		val tc = collectTailRecursionCalls(fn, { false }, { false }).ir
		if (tc.isNotEmpty()) cfgFresh().also { tailrecCtx = BirEmitter.TailrecCtx(tc, it, fn) } else null
	} else null
	// #6 the return POSTCONDITION (if any) is registered on fn's return-target symbol for the body emission, so a
	// genuine `return v` wraps v in a non-null bind-check-throw (BirEmitterStatements.kt IrReturn).
	val bodyStmts = withReturnPostcondition(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	tailrecCtx = savedTailrec
	val coreBody = if (tailrecStart != null) """{"k":"label","id":$tailrecStart}${if (bodyStmts.isNotEmpty()) ",$bodyStmts" else ""}""" else bodyStmts
	// #6 non-null parameter PRECONDITIONS run at entry, BEFORE the tailrec label so a self-tail-jump does not re-check.
	val body = (preconditionChecks(fn) + listOfNotNull(coreBody.takeIf { it.isNotEmpty() })).joinToString(",")
	if (extRecv != null) selfSubst.remove(extRecv)
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val ps = (listOfNotNull(selfParam) + paramsJsonList(fn.parameters, ownerFn = fn)).joinToString(",")
	activeSemanticOwner = savedSemanticOwner
	// `override fun toString()/equals()/hashCode()` emits the KOTLIN name + `objectOverride:true` (a pure-Kotlin
	// fact); bir2cir/ilemit map it onto the System.Object slot so CLR virtual dispatch (Console.WriteLine,
	// structural `==`) finds the override.
	val isAnySlot = isAnySlotMethod(fn)
	val emitName = fn.name.asString()
	val isOvr = isOverride || isAnySlot
	// Object-overrides / interface members must stay public for virtual dispatch. Every other declaration keeps its
	// Kotlin visibility; bir2cir authors caller-side UnsafeAccessors for valid file-private edges split across TypeDefs.
	val declaredVis = if (isAnySlot) "public" else visOf(fn)
	val vis = declaredVis
	val isAbstract = fn.modality == Modality.ABSTRACT && fn.body == null
	// Kotlin modifiers with no .NET analog -> stamped as [KotlinFunction] by ilemit so a consuming Kotlin module
	// can restore them (infix/operator call resolution). `final/open/abstract` ride .NET virtual-ness already.
	// Structured modifiers (spec §2.1): `mods.inline` = ilemit stamps [KotlinInlineBody] with this body (this method
	// def IS the body) so a consuming module can splice it — the only way a cross-module non-local `return` through
	// the lambda works. `mods.infix/operator` -> [KotlinFunction]. `mods.suspend` = the neutral coroutine FACT: kotc
	// does NO coroutine lowering (body emits plainly; suspend calls carry `"suspendCall":true`), the await/state-
	// machine/Task-ABI lowering is a DEFERRED downstream layer; `resultType` (its Kotlin result type) rides alongside.
	val mods = funModsJson(fn, isInlineWithLambda(fn))
	// Return nullability (`fun f(): String?`) rides the `ret` type node (`{t:nullable,of:...}` from the uniform
	// birType) — the decl-level `retNullable` flag is RETIRED. bir2cir/ilemit derive .NET NRT from the type node.
	val kotlinStatic = if (static && isKotlinStaticFunction(fn)) ""","kotlinStatic":true""" else ""
	return """{"name":${str(emitName)}${declarationIdField(fn)}${explicitClrNameField(fn)},"static":$static$kotlinStatic,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${isAnySlot},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}$mods${resultTypeJson(fn)}${companionReceiverField(fn, "function", fn.name.asString())},"params":[$ps],"ret":${birType(fn.returnType).toJson()}${retCtxFnTypeField(fn)},"body":[$body],"attrs":[${attrsJson(fn.annotations)}]${overridesJson(fn)}${posJson(fn)}}"""
}

/** Structured declaration-modifier object (spec §2.1): a single `"mods":{name:true,…}` carrying ONLY the set flags
 *  (absent key = not set), replacing the order-dependent `$kmods$inlineFlag$suspendField` fragment concatenation.
 *  `inline` = the "inline body must travel" fact (isInlineWithLambda), the only inline shape ilemit splices. */
internal fun BirEmitter.funModsJson(fn: IrSimpleFunction, inline: Boolean = false): String {
	val flags = buildList {
		if (inline) add(""""inline":true""")
		// `inlineOnly` = the `@kotlin.internal.InlineOnly` FACT translated to a flag — ilemit stamps
		// [MethodImpl(AggressiveInlining)] off it. This is a pure annotation read-translation, SEPARATE from `inline`
		// (isInlineWithLambda, the load-bearing [KotlinInline] splice signal bir2cir keys on, which stays narrow); it is
		// NOT keyed on general `isInline`.
		if (fn.annotations.any { it.type.classFqName?.asString() == "kotlin.internal.InlineOnly" }) add(""""inlineOnly":true""")
		if (fn.isInfix) add(""""infix":true""")
		if (fn.isOperator) add(""""operator":true""")
		if (fn.isSuspend) add(""""suspend":true""")
	}
	return if (flags.isEmpty()) "" else ""","mods":{${flags.joinToString(",")}}"""
}

/** A `suspend fun`'s Kotlin result type rides ALONGSIDE `mods.suspend` (it is a Type, not a modifier flag). */
internal fun BirEmitter.resultTypeJson(fn: IrSimpleFunction): String =
	if (fn.isSuspend) ""","suspendRet":${birType(fn.returnType).toJson()}""" else ""

/** Structured class-modifier object (spec §2.1): `"mods":{name:true,…}` for class-nature Kotlin facts
 *  (`fun`-interface, `sealed`, `annotation`, `value`, …) — only the set flags, absent = not set.
 *  `value` = a Kotlin `value`/inline class (`IrClass.isValue`). It USED to reach bir2cir via the
 *  `@kotlin.jvm.JvmInline` class annotation, but the 2.4.0 frontend no longer materializes that
 *  (OptionalExpectation `expect` annotation with no non-JVM actual) on kotc's metadata/native sessions —
 *  so the value-class fact must be carried as this pure-Kotlin modifier instead. bir2cir keys single-field
 *  value-class erasure off it (it owns the single-vs-multi-field lowering decision). */
internal fun BirEmitter.classModsJson(
	fnIface: Boolean = false,
	sealed: Boolean = false,
	annotation: Boolean = false,
	value: Boolean = false,
	objectSingleton: Boolean = false,
	inner: Boolean = false
): String {
	val flags = buildList {
		if (annotation) add(""""annotation":true""")
		if (fnIface) add(""""fun":true""")
		if (sealed) add(""""sealed":true""")
		if (value) add(""""value":true""")
		if (objectSingleton) add(""""object":true""")
		if (inner) add(""""inner":true""")
	}
	return if (flags.isEmpty()) "" else ""","mods":{${flags.joinToString(",")}}"""
}

/** An `inline fun` with at least one function-typed parameter (AXIS ①: `Fn`, INCLUDING `noinline` AND `crossinline`)
 *  — the inline shape whose body must travel for cross-module consumption, so it carries its `[KotlinInline]` inlineBir
 *  payload. Matches `callNeedsSplice`'s AXIS-① trigger: ANY lambda arg splices the fn, so its body (which references
 *  EVERY function param — a noinline one as a delegate temp, a normal/crossinline one via a spliced carrier) must be
 *  available. The per-arg carrier-vs-delegate split (AXIS ②) is decided in the emitters, not here. Lambda-less inline
 *  funs degrade to ordinary calls (the JIT inlines those). */
internal fun BirEmitter.isInlineWithLambda(fn: IrSimpleFunction): Boolean =
	fn.isInline && fn.parameters.any { it.kind == IrParameterKind.Regular && birType(it.type) is TypeNode.Fn }

// ===== Coroutine SUSPEND FACTS (kotc emits facts only; ALL coroutine lowering is bir2cir's) =====
// kotc does NO coroutine lowering. A `suspend fun`/lambda body emits PLAINLY: decls carry `"suspend":true`
// (+ `resultType`), suspend call sites carry `"suspendCall":true`, and a suspend lambda emits `newSuspendLambda`.
// bir2cir consumes those facts to build the `ContinuationImpl` state machine + the public `Task<T>` bridge; kotc
// bakes NO coroutine ABI. Platform await declarations arrive from reference KLIBs and therefore are never emitted as
// source declarations; their `ClrAwaitBridge` fact is copied onto the suspend call for bir2cir.

/**
 * `,"typeParams":[...]` for a generic class/interface/method (empty when non-generic). An unconstrained param
 * is a bare name string `"T"`; a bounded one (`<T : Comparable<T>>`) is `{"name":"T","constraints":[...]}`
 * (each constraint a BIR type, e.g. `clrg:System.IComparable[gp:T]`). `kotlin.Any` bounds are dropped.
 */
internal fun BirEmitter.typeParamDeclarationsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String {
	val entries = tps.joinToString(",") { tp ->
		// kotc emits the PURE-KOTLIN bound in EVERY build (#66): the `kotlin.Comparable` upper-bound DROP is a
		// SUBSTITUTION CONSEQUENCE (a substituted BCL primitive has no kotlin.Comparable bound), so it belongs to
		// bir2cir (StdlibSubstituteTypeParams, rt-build only), NOT here. `kotlin.Any` bounds are still dropped
		// (a pure-Kotlin fact — Any is the implicit top). Other bounds (clr/clrg) are kept.
		val bounds = tp.superTypes.filter { it.classFqName?.asString() != "kotlin.Any" }.map { birType(it) }
		// Declaration-site variance `out`/`in` -> CLR covariant/contravariant (ilemit applies it only on
		// interfaces, where the CLR allows variance; on classes it's Kotlin-level only — dropped).
		val variance = when (tp.variance) {
			org.jetbrains.kotlin.types.Variance.OUT_VARIANCE -> "out"
			// `in` (contravariant) is emitted verbatim here in EVERY build. The runtime DROP of `in` (the CLR's
			// variance-validity check is stricter than Kotlin's, e.g. Continuation<in T>.resumeWith(Result<out T>)) is
			// bir2cir's (StdlibSubstituteTypeParams, rt-build only).
			org.jetbrains.kotlin.types.Variance.IN_VARIANCE -> "in"
			else -> null
		}
		val reified = tp.isReified
		if (bounds.isEmpty() && variance == null && !reified) str(tp.name.asString())
		else {
			val parts = ArrayList<String>()
			parts.add(""""name":${str(tp.name.asString())}""")
			if (bounds.isNotEmpty()) parts.add(""""constraints":[${bounds.joinToString(",") { str(it) }}]""")
			if (variance != null) parts.add(""""variance":${str(variance)}""")
			if (reified) parts.add(""""reified":true""")
			"{${parts.joinToString(",")}}"
		}
	}
	return "[$entries]"
}

internal fun BirEmitter.typeParamsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String =
	if (tps.isEmpty()) "" else ""","typeParams":${typeParamDeclarationsJson(tps)}"""

internal fun BirEmitter.paramsJson(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): String =
	paramsJsonList(params).joinToString(",")

/** THE single projection predicate: a parameter that occupies a POSITIONAL slot in the emitted method. Kotlin has four
 *  `IrParameterKind`s and DotKt gives each exactly one physical form:
 *    - `DispatchReceiver` -> the CLR call receiver (`{k:this}` in the body), never a parameter slot;
 *    - `ExtensionReceiver` -> the LEADING `__self` parameter, prepended by each declaration site;
 *    - `Context` and `Regular` -> ordinary positional value parameters, in `IrFunction.parameters` order (so every
 *      context parameter precedes every regular one, matching fir2ir's ordering).
 *  So the emitted physical sequence of ANY function is `[__self?] + contexts + regulars`, and the declaration's `params`,
 *  the call's `args`, the `sig`/`paramSig` overload key and the `@KotlinDefault` index all count that ONE sequence. A
 *  `Regular`-only filter is correct ONLY for a fact that is genuinely about regular parameters (can carry a default,
 *  `vararg`, `noinline`, the `main` entry-point shape) — never for building a parameter, argument, signature or index
 *  vector, where dropping a context parameter on one side and not the other is what makes a call miscompile. */
internal fun BirEmitter.isValueParameter(p: IrValueParameter): Boolean =
	p.kind == IrParameterKind.Regular || p.kind == IrParameterKind.Context

/** `,"ctxFnType":N` for a declaration slot whose Kotlin TYPE is a CONTEXT function type — N = how many of that
 *  function type's LEADING physical arguments are contexts (`context(A) B.(D) -> E` is
 *  `@ExtensionFunctionType Function3<A,B,D,E>`, so N=1: contexts first, then the receiver, then the value params).
 *
 *  It is a SLOT fact rather than a field of the type node because a type node is rebuilt by a dozen bir2cir passes
 *  (ReferenceNullableStrip, NullableGenericErasure, …), any of which would silently drop it — the same reason
 *  `suspendFnType`/`retNullableFlags` are slot facts. bir2cir turns it into `[KotlinContextFunctionType(N)]`.
 *
 *  fir2ir ERASES the arity: `context(A) B.(D) -> E` and `B.(A, D) -> E` are the SAME IrType, so the number cannot be
 *  read off `IrType` at all. It comes from the FIR capture ([kotc.frontend.ClrContextFnTypes]), keyed by this slot's
 *  source offset (fir2ir copies the same PSI offsets onto the IR declaration). Empty for every ordinary slot. */
internal fun ctxFnTypeField(ctxCount: Int): String =
	if (ctxCount > 0) ""","ctxFnType":$ctxCount""" else ""

/** The context-function-type arity of a PARAMETER slot, with the property fallback a default SETTER needs.
 *
 *  A default accessor's IR range is the property HEADER (fir2ir deliberately excludes the initializer), so a default
 *  setter's `value` parameter has no range of its own that matches what the capture recorded, and
 *  `var block: context(A) () -> Unit` lost the fact on `set_block` while `get_block` kept it (the return path already
 *  falls back to the property). The setter's parameter type IS the property's type, so the property's own fact is the
 *  right answer rather than an approximation. */
internal fun BirEmitter.ctxFnCountFor(p: IrValueParameter): Int {
	val own = kotc.frontend.ClrContextFnTypes.contextCountAt(sourcePathOf(p), p.startOffset, p.endOffset)
	if (own > 0) return own
	val fn = p.parent as? IrSimpleFunction ?: return 0
	val prop = fn.correspondingPropertySymbol?.owner ?: return 0
	if (prop.setter !== fn) return 0
	return kotc.frontend.ClrContextFnTypes.returnContextCountAt(sourcePathOf(prop), prop.startOffset, prop.endOffset)
}

/** The RETURN-position twin of [ctxFnTypeField]: `,"retCtxFnType":N` for a declaration whose RETURN type is a
 *  context function type (`fun make(): context(A) B.() -> C`, and a property accessor's restored type). A property
 *  accessor is keyed by its OWN source offset when it has one and by the property's otherwise — a default accessor
 *  is synthesized and carries the property's. */
internal fun BirEmitter.retCtxFnTypeField(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String {
	val own = kotc.frontend.ClrContextFnTypes.returnContextCountAt(sourcePathOf(fn), fn.startOffset, fn.endOffset)
	// A property's own range answers for its GETTER, whose Kotlin return type IS the property's. A SETTER returns
	// Unit, so it must never inherit that fact — the fallback is gated on the accessor actually being the getter.
	val prop = (fn as? IrSimpleFunction)?.correspondingPropertySymbol?.owner
	val n = if (own > 0) own
		else if (prop != null && prop.getter === fn)
			kotc.frontend.ClrContextFnTypes.returnContextCountAt(sourcePathOf(prop), prop.startOffset, prop.endOffset)
		else 0
	return if (n > 0) ""","retCtxFnType":$n""" else ""
}

/** The source file an IR declaration came from, as the FIR capture recorded it — the file half of the
 *  [kotc.frontend.ClrContextFnTypes] key. Null for a synthetic declaration with no source (which has no fact). */
internal fun BirEmitter.sourcePathOf(node: IrElement): String? {
	// A referenced declaration may belong to another source file. locationOf() is relative to the file
	// currently being emitted, so using it for declarations can alias unrelated FIR facts by offset.
	if (node is IrDeclaration) {
		var owner = node.parent
		while (owner is IrDeclaration) owner = owner.parent
		return (owner as? IrFile)?.fileEntry?.name
	}
	return locationOf(node)?.path
}

/** The `companionReceiver` declaration field for a Kotlin 2.4 COMPANION EXTENSION (`companion fun C.foo()`,
 *  `companion val C.bar`): the type the receiverless static declaration is semantically associated with.
 *
 *  Like `ctxFnType`, this is NOT read off IR — fir2ir drops the extension-receiver parameter of a companion
 *  extension entirely, so the fact comes from the FIR capture ([kotc.frontend.ClrCompanionExtensions]), keyed by
 *  this declaration's source path and END offset. Empty for every declaration that is not one, which is nearly all
 *  of them.
 *
 *  It is a Kotlin fact only — WHICH CLR type physically hosts the declaration stays the next layer's decision.
 *
 *  Carried as a JSON STRING rather than an inline type node, for the same reason `kotlinType`/`collIdentity` are:
 *  the association names a KOTLIN type and must survive CLR type lowering untouched. A `companion fun String.f()`
 *  has to still read `kotlin.String` when the consuming module restores it, not `System.String`. */
internal fun BirEmitter.companionReceiverJson(decl: org.jetbrains.kotlin.ir.IrElement): String? {
	val own = kotc.frontend.ClrCompanionExtensions.receiverTypeJsonAt(sourcePathOf(decl), decl.endOffset)
	if (own != null) return own
	val property = (decl as? IrSimpleFunction)?.correspondingPropertySymbol?.owner ?: return null
	return kotc.frontend.ClrCompanionExtensions.receiverTypeJsonAt(sourcePathOf(property), property.endOffset)
}

internal fun BirEmitter.companionReceiverField(
	decl: org.jetbrains.kotlin.ir.IrElement,
	kind: String,
	sourceName: String,
): String = companionReceiverJson(decl)?.let {
	""","companionReceiver":${str(it)},"companionMemberKind":${str(kind)},"companionSourceName":${str(sourceName)}"""
} ?: ""

/** Same-module use-site identity for a receiverless companion extension call. fir2ir can replace the resolved
 * declaration with a synthetic wrapper that has no source slot, so fall back to the FIR-captured use expression. */
internal fun BirEmitter.companionReceiverCallTag(
	decl: org.jetbrains.kotlin.ir.IrElement,
	use: org.jetbrains.kotlin.ir.IrElement? = null,
): String = companionReceiverCallJson(decl, use)?.let {
	""","companionReceiver":${str(it)}"""
} ?: ""

internal fun BirEmitter.companionReceiverCallJson(
	decl: org.jetbrains.kotlin.ir.IrElement,
	use: org.jetbrains.kotlin.ir.IrElement? = null,
): String? {
	val receiver = companionReceiverJson(decl)
		?: (decl as? org.jetbrains.kotlin.ir.declarations.IrDeclaration)
			?.let { companionExtensionReceiverType(it) }
			?.let { birType(it).toJson() }
		?: use?.let {
		kotc.frontend.ClrCompanionExtensions.receiverTypeJsonAtUse(sourcePathOf(it), it.startOffset, it.endOffset)
	}
	return receiver
}

/** Callable-reference use-site twin. fir2ir's reference wrapper does not retain declaration offsets. */
internal fun BirEmitter.companionReceiverUseTag(use: org.jetbrains.kotlin.ir.IrElement): String {
	val hit = kotc.frontend.ClrCompanionExtensions.receiverTypeJsonAtUse(sourcePathOf(use), use.startOffset, use.endOffset)
	return hit?.let { ""","companionReceiver":${str(it)}""" } ?: ""
}

/** How many parameters `fn` EMITS: the [isValueParameter] physical sequence with the extension receiver's leading
 *  `__self` counted first. The one number to hand any consumer that compares against a .NET parameter count. */
internal fun BirEmitter.emittedParamCount(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): Int =
	(if ((extensionReceiverParam(fn) != null)) 1 else 0) +
		fn.parameters.count { isValueParameter(it) }

/** THE 2-TIER default-argument test (docs/dotkt-semantics.md): can the parameter's OWN CLR type carry its default as
 *  a `[DefaultParameterValue]` constant? TRUE (Tier 1) — a primitive/char/bool const on its primitive param, a String
 *  const on a `String` param, or a null const on any nullable/reference param → native `[Optional]`+`[DefaultParameterValue]`.
 *  FALSE (Tier 2) — a String const on a NON-String reference param (`CharSequence`: a string constant cannot sit on an
 *  interface-typed param), or ANY non-constant default → `@KotlinDefault(bir)` + a REQUIRED param (a kcc consumer
 *  splices the expression, a C# consumer passes the arg explicitly). */
internal fun BirEmitter.isMetadataRepresentableDefault(p: org.jetbrains.kotlin.ir.declarations.IrValueParameter): Boolean {
	val expression = p.defaultValue?.expression ?: return false
	if (expression is IrGetEnumValue) {
		val enumClass = expression.symbol.owner.parent as? IrClass ?: return false
		return !isRichEnum(enumClass)
	}
	val def = expression as? org.jetbrains.kotlin.ir.expressions.IrConst ?: return false
	val v = def.value
	return when {
		v == null -> true                                                   // null fits any nullable/reference param (ldnull)
		v is String -> p.type.classFqName?.asString() == "kotlin.String"    // a string const only fits a String param
		else -> true                                                        // a primitive/char/bool const on its primitive param
	}
}

/** True if `fn` carries `@KotlinDefault` on ALL its defaulted params — the UNIFORM per-parameter splice source
 *  bir2cir uses to fill an omitted arg POSITIONALLY (Tier-1 and Tier-2 alike). Two consumers read the carrier:
 *  DefaultArgSplice at a cross-module callStatic/callInstance, and InlineSplice STEP 5 at a callInline body splice.
 *  A CONSTRUCTOR carries it too (#235), consumed by DefaultArgSplice at a cross-module `new` (keyed
 *  `<type>|.ctor|<emitted arg count>`). Without the attribute dll2klib would surface the param REQUIRED and the
 *  omission would not even resolve at the consumer's frontend.
 *  This MUST cover Tier-1 too: at a CROSS-MODULE call kotc sees the callee's default as an IrErrorExpression (the
 *  frontend KLIB drops the VALUE) and so cannot tell Tier-1 from Tier-2 — it emits a `defaultArg` placeholder for EVERY
 *  omitted default, which bir2cir can only fill if a `@KotlinDefault` exists for that slot. (Tier-1 params ALSO keep the
 *  native `[Optional]` + `[DefaultParameterValue]` metadata for a C#/VB/F# consumer — unchanged; `@KotlinDefault` is the
 *  kcc-consumer splice source, ref.dll-only, stripped from the runtime build.)
 *
 *  Coverage is every function and constructor with a defaulted regular parameter. This must not be restricted by
 *  dispatch/suspend/inline shape: a metadata reference declaration may deliberately retain a Kotlin frontend type
 *  (`kotlin.Int`) that cannot legally carry an ECMA-335 `int32` constant, so the native constant is not a universal
 *  carrier even for `= 0`. The ref-DLL-only KotlinDefault expression is the uniform source for bir2cir; the runtime
 *  build strips it. The one genuinely-unsafe default SHAPE — one that reads an enclosing-instance (dispatch or outer)
 *  receiver — is poisoned per-expression in [defaultCarrierBir] rather than narrowing declaration coverage. */
internal fun BirEmitter.carriesKotlinDefault(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): Boolean =
	fn.parameters.any { it.kind == IrParameterKind.Regular && it.defaultValue != null }

/** The SYNTHETIC `copy` of a data class — the one whose omitted parameter defaults are `this.<field>` by construction.
 *  Name + `isData` parent is NOT enough: only the generated SIGNATURE is reserved, so a data class may also declare a
 *  differently-signed `copy` OVERLOAD of its own (`data class D(val x: Int) { fun copy(tag: String, z: Int = x * 2) }`
 *  compiles and runs), whose defaults are ordinary expressions and are NOT field reads. The generated one mirrors the
 *  primary constructor parameter-for-parameter, name-for-name AND type-for-type (both signatures are written in the
 *  class's own type-parameter frame, so the `birType` identities compare directly) — exactly the property a
 *  `this.<field>` reconstruction depends on.
 *
 *  UNMEASURED: the mis-selection this excludes is reasoned from the reconstruction's precondition, not observed. It
 *  needs a data class carrying a `copy` OVERLOAD in a REFERENCED module, and the only cross-module source that
 *  preserves the `data` nature is the frontend KLIB — the stdlib, which declares no such class. The same-module case
 *  (verified to compile and run) never reaches the reconstruction, because its default is real IR. */
internal fun BirEmitter.isDataClassCopy(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean {
	if (fn.name.asString() != "copy") return false
	val cls = fn.parent as? IrClass ?: return false
	if (!cls.isData) return false
	val ctorParams = cls.primaryConstructor?.parameters?.filter { it.kind == IrParameterKind.Regular } ?: return false
	val copyParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }
	return ctorParams.size == copyParams.size && ctorParams.indices.all {
		ctorParams[it].name == copyParams[it].name && birType(ctorParams[it].type) == birType(copyParams[it].type)
	}
}

/** #146: the @KotlinDefault carrier BIR for a default expression, made CLOSED so bir2cir can re-emit it at a
 *  cross-module omitted call site. A constant / simple call (`= emptyList()`) emits NO lifted method, so its BIR is
 *  carried verbatim (byte-identical to the #134 constant carrier). A NON-CAPTURING lambda default (`= {}`, the
 *  Avalonia `configure: Panel.() -> Unit = {}` idiom) lifts a generated static method into THIS file's `liftedMethods`
 *  and returns a `newDelegate` referencing it — an OPEN term (the method is library-local). Detach that lift DELTA from
 *  this library's file class (it is dead here — only the default's call sites materialize it) and wrap it with its
 *  `newDelegate` in a `defaultCarrier` envelope; bir2cir's DefaultArgSplice re-hoists the carried method app-local (fresh
 *  name) at the consumer. Capturing closures, SAMs and suspend lambdas already carry their raw synthesis facts on their
 *  construction node (`synthClass`, captures/body, or suspend-lambda descriptor), so they need no second envelope.
 *
 *  Receiver reads are closed by [paramsJsonList] before this renderer runs. The carrier therefore contains explicit
 *  `{k:defaultArgReceiver,kind:dispatch|extension|enclosing}` leaves rather than the ambiguous ordinary BIR `{k:this}`.
 *  A nested closure/SAM/suspend-lambda's OWN `{k:this}` remains ordinary BIR and is never mistaken for the callee's
 *  receiver by the consumer-side token substitution. */
internal fun BirEmitter.defaultCarrierBir(def: org.jetbrains.kotlin.ir.expressions.IrExpression): String {
	val before = liftedMethods.size
	// This is a second declaration projection, independent of a same-module call site's projection of the same IR.
	// Give lexical local functions fresh ids here; the primary ids remain installed for the executable expression.
	val bir = withClonedLocalFunctionIds(def) { expr(def) }
	val delta = if (liftedMethods.size > before) {
		val d = ArrayList(liftedMethods.subList(before, liftedMethods.size))
		while (liftedMethods.size > before) liftedMethods.removeAt(liftedMethods.size - 1)
		d
	} else emptyList()
	if (delta.isEmpty()) return bir
	return """{"k":"defaultCarrier","expr":$bir,"lifted":[${delta.joinToString(",")}]}"""
}

internal fun BirEmitter.paramsJsonList(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>,
		ownerFn: org.jetbrains.kotlin.ir.declarations.IrFunction? = null): List<String> {
	// A `@KotlinDefault(index, bir)` on each defaulted param of a qualifying function: `index` = the param's position
	// in the emitted call (extension receiver first, if any), `bir` = the default expression as a BIR-json STRING (so
	// bir2cir splices it PRE-lowering; it is opaque to this build's type lowering). Stamped on ALL defaulted params of
	// a Tier-2-carrying function (uniform splice source). kotc emits ONE BIR for every build: the attr rides every
	// build; the rt build strips it downstream (ilemit param-attr strip under `--build-stdlib=runtime`).
	val emitKotlinDefault = ownerFn != null && carriesKotlinDefault(ownerFn)
	// The stamped `index` is the param's position in the EMITTED call's arg array, so whatever rides ahead of the
	// value args counts first: an extension receiver (`__self`), or — for an INNER-class ctor — the enclosing
	// instance the `new` passes as its leading arg. (Context parameters need no offset: they ARE value params here,
	// numbered in place ahead of the regulars, and [filledArgs] emits them in the same positions.)
	val extOffset = when {
		ownerFn?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true -> 1
		ownerFn is IrConstructor && (ownerFn.parent as? IrClass)?.isInner == true -> 1
		else -> 0
	}
	val valueParams = params.filter { isValueParameter(it) }
	// Close every declaration-scoped value a carrier may read into an EXPLICIT call-site token. Parameters use their
	// emitted position. Receiver kinds do not: a member extension has BOTH dispatch and extension receivers, while an
	// inner constructor has an enclosing instance but no constructed `this` yet. Collapsing those facts into ordinary
	// `{k:this}` was the common cause of #34/#42.
	//
	// An inner/member body reaches an OUTER `this` through an ambient captureSubst such as
	// `field(recv=this,name=__outer)`. Rewrite only that ROOT `this` to the dispatch token while carrying the default;
	// nested closure/SAM bodies keep their own ordinary `{k:this}` untouched. For an INNER constructor the immediate
	// outer instance is already the hidden leading argument, so bind that declaration directly to `enclosing`.
	val savedCarrierSubst = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration, String?>()
	fun installCarrierSubst(d: org.jetbrains.kotlin.ir.declarations.IrValueDeclaration, json: String) {
		if (!savedCarrierSubst.containsKey(d)) savedCarrierSubst[d] = captureSubst[d]
		captureSubst[d] = json
	}
	if (emitKotlinDefault) {
		valueParams.forEachIndexed { valueIdx, vp ->
			installCarrierSubst(vp, """{"k":"defaultArgParam","idx":${valueIdx + extOffset}}""")
		}
		ownerFn.parameters.firstOrNull { it.kind == IrParameterKind.DispatchReceiver }?.let {
			installCarrierSubst(it, """{"k":"defaultArgReceiver","kind":"dispatch"}""")
		}
		extensionReceiverParam(ownerFn)?.let {
			installCarrierSubst(it, """{"k":"defaultArgReceiver","kind":"extension"}""")
		}
		val ownerClass = ownerFn.parent as? IrClass
		val immediateOuter = ownerClass?.takeIf { it.isInner }?.parent as? IrClass
		var p: IrClass? = ownerClass
		while (p != null) {
			p.thisReceiver?.let { recv ->
				val prior = captureSubst[recv]
				when {
					ownerFn is IrConstructor && p === immediateOuter ->
						installCarrierSubst(recv, """{"k":"defaultArgReceiver","kind":"enclosing"}""")
					p === ownerClass && prior == null ->
						installCarrierSubst(recv, """{"k":"defaultArgReceiver","kind":"dispatch"}""")
					prior != null && prior.contains("""{"k":"this"}""") ->
						installCarrierSubst(recv, prior.replace(
							"""{"k":"this"}""",
							"""{"k":"defaultArgReceiver","kind":"dispatch"}"""))
				}
			}
			p = p.parent as? IrClass
		}
	}
	val result = try { valueParams
		.mapIndexed { valueIdx, it ->
			// `vararg xs: T` -> mark the param so ilemit stamps [ParamArray] (native .NET varargs; a cross-module
			// consumer can then call `f(1, 2, 3)`). `context` marks a Kotlin CONTEXT parameter: physically an ordinary
			// positional parameter, but a consuming Kotlin module must restore it AS a context parameter, else the callee's
			// SOURCE shape changes at the module boundary (`with(s) { f(1) }` would have to become `f(s, 1)`). bir2cir turns
			// the flag into the `[KotlinContextParameter]` marker dll2klib reads back. Param nullability rides the `type`
			// node itself (`{t:nullable,of:...}` from the uniform birType) — the decl-level `nullable` flag is RETIRED.
			val modFlags = listOfNotNull(
				"\"vararg\":true".takeIf { _ -> it.varargElementType != null },
				"\"context\":true".takeIf { _ -> it.kind == IrParameterKind.Context },
			)
			val vararg = if (modFlags.isEmpty()) "" else ""","mods":{${modFlags.joinToString(",")}}"""
			// TIER 1 — a metadata-representable default -> carry it so ilemit stamps [Optional]+[DefaultParameterValue]
			// (a C# OR kcc caller can omit the arg; ilemit's EmitDefaultArg fills it from the .NET metadata). A TIER-2
			// default carries NO `default` field, so the param is emitted REQUIRED (no [Optional]) — a C# caller must
			// pass it; a kcc caller relies on the @KotlinDefault splice below.
			val default = if (isMetadataRepresentableDefault(it)) ""","default":${expr(it.defaultValue!!.expression)}""" else ""
			// PARAMETER-level annotations -> .NET custom attributes on the emitted parameter (e.g. @ClrRefArgument,
			// which bir2cir reads from the ref.dll to pass the arg by reference). attrsJson is stripped in the runtime
			// build (`--build-stdlib=runtime`), so param attrs ride only the ref.dll — exactly bir2cir's read surface.
			val srcAttrs = attrsJson(it.annotations)
			val kotlinDefault = if (emitKotlinDefault) it.defaultValue?.expression?.let { def ->
				val bir = defaultCarrierBir(def)   // BIR of the default (real IR — the callee's own build), CLOSED for cross-module splice
				"""{"attr":${fqnJson("kotlin.clr.KotlinDefault")},"argTypes":[${fqnJson("kotlin.Int")},${fqnJson("kotlin.String")}],"args":[{"k":"const","type":${fqnJson("kotlin.Int")},"value":${valueIdx + extOffset}},{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(bir)}}]}"""
			} else null
			val allAttrs = listOfNotNull(srcAttrs.takeIf { s -> s.isNotEmpty() }, kotlinDefault).joinToString(",")
			val pattrs = if (allAttrs.isNotEmpty()) ""","attrs":[$allAttrs]""" else ""
			val ctxFn = ctxFnTypeField(ctxFnCountFor(it))
			"""{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}$vararg$default$ctxFn$pattrs}"""
		}
	} finally {
		savedCarrierSubst.forEach { (d, prev) ->
			if (prev != null) captureSubst[d] = prev else captureSubst.remove(d)
		}
	}
	return result
}

/** A `,"sig":[<TypeNode>,...]` field carrying the frontend-resolved declaration's parameter vector into BIR.
 *  Emit it ALWAYS: for a non-overloaded callee it is still the exact declaration identity, and emitting
 *  unconditionally avoids any overload-detection edge case. bir2cir projects this fact into the physical CIR member
 *  descriptor; ilemit only links that descriptor. The signature
 *  MATCHES how `method()` lays out the def's `params` — the [isValueParameter] physical sequence `[ext receiver?] +
 *  contexts + regulars`, each `birType` — the #37 m3b type-path structuring: sig is a STRUCTURED TypeNode array (the same
 *  `birType(...).toJson()` path every other type slot uses), NOT a legacy comma-string. Omitting the context parameters
 *  here used to make the descriptor one arity short and let the old emitter's name fallback emit a short argument list
 *  (invalid IL); the resolved-CIR contract now rejects such an incomplete identity. */
internal fun BirEmitter.overloadSigField(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String {
	val ext = extensionReceiverParam(fn)?.let { birType(it.type) }
	val vals = fn.parameters.filter { isValueParameter(it) }.map { birType(it.type) }
	return ""","sig":[${(listOfNotNull(ext) + vals).joinToString(",") { it.toJson() }}]"""
}

/** True iff `fn` is an override of one of kotlin.Any's three universal methods (the CLR System.Object slots) —
 *  toString()/hashCode() (arity 0) or equals(Any?) (arity 1) with a dispatch receiver and no extension receiver. The
 *  BCL slot NAME (ToString/GetHashCode/Equals) is bir2cir's concern (ObjectSlotRename); kotc emits only this fact.
 *  Only a REAL instance-member override qualifies. A top-level / EXTENSION function named `hashCode`/`toString`
 *  (e.g. `Any?.hashCode()`, `Any?.toString()`) is NOT an Object override — if it were later renamed to the slot
 *  name it would make a STATIC method on the file class collide with the inherited Object slot (TypeLoad "do not
 *  match", e.g. HashCodeKt/LibraryKt). Require a dispatch receiver + no extension receiver. */
internal fun BirEmitter.isAnySlotMethod(fn: IrSimpleFunction): Boolean {
	val hasDispatch = fn.parameters.any { it.kind == IrParameterKind.DispatchReceiver }
	val hasExt = (extensionReceiverParam(fn) != null)
	if (!hasDispatch || hasExt) return false
	// [isValueParameter], not `Regular`: `context(c: C) fun toString(): String` is legal Kotlin and is NOT the
	// universal slot — it takes an argument on the CLR. Counting only the regular parameters made it `reg == 0`,
	// renaming `ToString(C)` onto System.Object's slot (the exact arity gate bir2cir's AnySlotRebind relies on).
	val reg = fn.parameters.count { isValueParameter(it) }
	return when (fn.name.asString()) {
		"toString", "hashCode" -> reg == 0
		"equals" -> reg == 1
		else -> false
	}
}
