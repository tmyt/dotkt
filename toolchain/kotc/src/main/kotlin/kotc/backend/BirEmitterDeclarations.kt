package kotc.backend

import kotc.bir.TypeNode
import org.jetbrains.kotlin.backend.common.collectTailRecursionCalls
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrField
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrProperty
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
import org.jetbrains.kotlin.ir.expressions.IrFunctionReference
import org.jetbrains.kotlin.ir.expressions.IrGetClass
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

internal fun BirEmitter.interfaceDef(iface: IrClass): String {
	fun ifaceMethod(fn: IrSimpleFunction, prop: IrProperty? = fn.correspondingPropertySymbol?.owner): String {
		// C3b reverse direction: a Kotlin interface extending a @Clr interface (Set : Collection->IReadOnlyCollection).
		// kotc emits the plain Kotlin `get_size` here for both ref and rt — the BCL override-slot rename
		// (get_size -> get_Count) is bir2cir's DeclarationRename off the ref.dll @ClrIntrinsic.
		val name = prop?.let { p -> (if (fn == p.getter) "get_" else "set_") + p.name.asString() } ?: fn.name.asString()
		val isSetter = prop != null && fn == prop.setter
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
		val extRecv = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
		// #6 non-null parameter PRECONDITIONS + return POSTCONDITION for a default interface method body (an abstract slot
		// has no body to guard).
		val body = if (hasDefault) {
			val stmts = withReturnPostcondition(fn) { (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
			(preconditionChecks(fn) + listOfNotNull(stmts.takeIf { it.isNotEmpty() })).joinToString(",")
		} else ""
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
		// Kotlin 2.4 does not consistently expose inherited declarations through the isFakeOverride convenience flag
		// after FIR2IR (notably for an interface inheriting a declaration injected from a klib).  Prefer its typed IR
		// origin when present; otherwise the shape is still unambiguous: an override closure, no body, and no source offset.
		val inheritedSynthetic = fn.isFakeOverride || fn.origin == IrDeclarationOrigin.FAKE_OVERRIDE ||
			(fn.body == null && fn.overriddenSymbols.isNotEmpty() && fn.startOffset < 0)
		val fakeOverride = if (inheritedSynthetic) ",\"fakeOverride\":true" else ""
		return """{"name":${str(name)},"static":false,"override":false,"virtual":true$fakeOverride${typeParamsJson(fn.typeParameters)},"params":[$params],"ret":${str(ret)}${funModsJson(fn)}${resultTypeJson(fn)},"body":[$body],"attrs":[$memberAttrs]${overridesJson(fn)}}"""
	}
	val funMethods = iface.declarations.filterIsInstance<IrSimpleFunction>()
		// equals/hashCode/toString are inherited from Any into every Kotlin interface (fake overrides). On the CLR
		// System.Object already provides Equals/GetHashCode/ToString, so emitting them as interface members creates
		// abstract slots no implementer fills (the lowercase Kotlin name never binds Object's) -> TypeLoadException.
		.filterNot { it.name.asString() in setOf("equals", "hashCode", "toString") }
		// A FAKE-OVERRIDE of a DEFAULT interface method (a DIM body lives in a supertype, e.g. Map.getOrDefault) must
		// NOT be re-emitted as an abstract slot here — that shadows the inherited DIM, so concrete implementers
		// (EmptyMap/MapWithDefaultImpl) "do not have an implementation". Abstract fake-overrides (no body anywhere, the
		// C3a size/get case) are KEPT (resolveFakeOverride has no body), so the BCL member binding still emits.
		.filterNot { it.isFakeOverride && it.resolveFakeOverride()?.body != null }
		.map { ifaceMethod(it) }
	val propMethods = iface.declarations.filterIsInstance<IrProperty>()
		.flatMap { p -> listOfNotNull(p.getter?.let { ifaceMethod(it, p) }, p.setter?.let { ifaceMethod(it, p) }) }
	// An interface's PLAIN companion object flattens to the interface's own statics — identical to the class path
	// (BirEmitter.kt companionObjectTypeName / classDef statFields). A CLR interface legally carries static fields
	// (run in the interface's .cctor) and static methods, so `interface I { companion object { val X = f() } }` and
	// its access `I.X` (a staticField on I) resolve, instead of the companion members being silently dropped (#83:
	// SharingStarted.Eagerly). A SUPER-TYPED companion is a separate lifted singleton, not flattened here.
	val companion = if (superTypedCompanion(iface) != null) null
		else iface.declarations.filterIsInstance<IrClass>().firstOrNull { it.isCompanion }
	// Companion non-const `val`/`var` -> static fields (initializer run in the .cctor); `const` is inlined at use.
	val statFields = companion?.declarations?.filterIsInstance<IrProperty>()?.mapNotNull { p ->
		val bf = p.backingField ?: return@mapNotNull null
		if (p.isConst) return@mapNotNull null
		val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()},"static":true,"init":$init${volatileFieldFlag(p)}}"""
	}.orEmpty()
	// Companion methods -> static methods of the interface; a companion property's CUSTOM accessor -> a static
	// get_/set_ on the interface (both mirror classDef's statMethods/companionAccessors).
	val statMethods = companion?.declarations?.filterIsInstance<IrSimpleFunction>()
		?.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && it.body != null }
		?.map { method(it, static = true) }.orEmpty()
	val companionAccessors = companion?.declarations?.filterIsInstance<IrProperty>()
		?.flatMap { p ->
			listOfNotNull(
				p.getter?.takeIf { fieldRoutedProperty(p) && !hasDefaultGetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
				p.setter?.takeIf { fieldRoutedProperty(p) && !hasDefaultSetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
		}.orEmpty()
	val methods = (funMethods + propMethods + statMethods + companionAccessors).distinct().joinToString(",")
	// 2B layer 1: a Kotlin interface property -> a REAL CLR property (PropertyBuilder over its get_/set_ interface
	// methods), so a consumer (facadegen restoring the ref assembly) sees `size` as a PROPERTY, not a bare get_size
	// method. The accessor methods are already emitted (propMethods) named get_<n>/set_<n>; wire the property over them.
	val ifaceProps = iface.declarations.filterIsInstance<IrProperty>().filter { it.getter != null }.joinToString(",") { p ->
		val n = p.name.asString()
		val setName = if (p.setter != null) str("set_$n") else "null"
		"""{"name":${str(n)},"type":${birType(p.getter!!.returnType).toJson()},"get":${str("get_$n")},"set":$setName}"""
	}
	// A nested interface (`TimeSource.WithComparableMarks`) -> a real CLR nested type of its enclosing class/interface.
	val nestedIn = emittedNestedParent(iface)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && !isExternalNetType(it) }
		?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
	val ifaces = iface.superTypes
		.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
		.mapNotNull { st ->
			val bt = birType(st)
			val stClass = st.classifierOrNull?.owner as? IrClass
			when {
				bt is TypeNode.Fn -> null
				stClass != null && isExternalNetType(stClass) -> bt.toJson()
				else -> stClass?.let { ownerSpec(it, st).toJson() }
			}
		}
		.joinToString(",")
	// Round-trip class-nature facts (Kotlin, not CLR) as structured `mods` (spec §2.1): `fun interface` (SAM) and
	// `sealed` — carried so a re-consuming Kotlin module can restore them (ilemit stamps [KotlinFunInterface]/
	// [KotlinSealed]; a plain CLR interface loses both).
	val funSealed = classModsJson(fnIface = iface.isFun, sealed = iface.modality == Modality.SEALED)
	return """{"name":${str(typeName(iface))},"kind":"interface"$nestedIn$funSealed${typeParamsJson(iface.typeParameters)},"base":null,"interfaces":[$ifaces],"fields":[${statFields.joinToString(",")}],"ctors":[],"methods":[$methods],"properties":[$ifaceProps],"attrs":[${attrsJson(iface.annotations)}]}"""
}

/** A Kotlin `enum class` -> a real .NET enum (ilemit DefineEnum + literals). */
internal fun BirEmitter.enumDef(e: IrClass): String {
	val entries = e.declarations.filterIsInstance<IrEnumEntry>()
		.mapIndexed { i, ent -> """{"name":${str(ent.name.asString())},"ordinal":$i}""" }
	val nestedIn = emittedNestedParent(e)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && !isExternalNetType(it) }
		?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
	return """{"name":${str(typeName(e))},"kind":"enum"$nestedIn,"entries":[${entries.joinToString(",")}]}"""
}

/** A "rich" enum has ctor params, user instance methods, or per-entry bodies -> can't be a CLR enum. */
internal fun BirEmitter.isRichEnum(ec: IrClass): Boolean {
	if (ec.kind != ClassKind.ENUM_CLASS) return false
	val ctorParams = ec.declarations.filterIsInstance<IrConstructor>()
		.any { c -> c.parameters.any { it.kind == IrParameterKind.Regular } }
	val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.any { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
	val entryBodies = ec.declarations.filterIsInstance<IrEnumEntry>().any { it.correspondingClass != null }
	return ctorParams || userMethods || entryBodies
}

/**
 * A rich enum -> a plain class with static singleton instances (JVM-style; Codex-confirmed). Fields:
 * `__name`/`__ordinal` (Kotlin Enum metadata) + user props; per-entry `static readonly` field initialized
 * in the `.cctor`; `toString`->`__name`; `values()`->fresh array; `valueOf(name)`->linear match.
 */
internal fun BirEmitter.richEnumDef(ec: IrClass): String {
	val name = typeName(ec)
	val entries = ec.declarations.filterIsInstance<IrEnumEntry>()
	val primaryCtor = ec.declarations.filterIsInstance<IrConstructor>().first { it.isPrimary }
	val userParams = primaryCtor.parameters.filter { it.kind == IrParameterKind.Regular }
	// User properties follow the CLR property model exactly like typeDef: the access site emits
	// `callInstance get_<name>` (there is no rich-enum special case for user props — only name/ordinal
	// route to the __name/__ordinal fields), so the class must carry real get_/set_ accessors + a
	// `properties` entry, with the backing field demoted to internal. A bare public field alone crashes
	// ilemit with "<Enum>.get_<prop> not found".
	// Only REAL user properties: kotlin.Enum's `name`/`ordinal` ride along as body-less fake overrides and
	// `entries` as an IrSyntheticBody getter (call sites route all three to __name/__ordinal/values());
	// emitting their accessors would produce empty methods (ilverify ReturnMissing). Gate on an IrBlockBody
	// getter/setter — exactly what accessorMethod can emit.
	val userProps = ec.declarations.filterIsInstance<IrProperty>().filter { !it.isFakeOverride }
	fun emitsGet(p: IrProperty) = p.getter?.body is IrBlockBody && !p.isConst && !p.isLateinit && !p.isDelegated && !isClrField(p)
	fun emitsSet(p: IrProperty) = p.setter?.body is IrBlockBody && !p.isConst && !p.isLateinit && !p.isDelegated && !isClrField(p)
	val userFields = userProps.mapNotNull { p ->
		val bf = p.backingField ?: return@mapNotNull null
		val visJson = if (emitsGet(p)) ""","vis":"internal"""" else ""
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()}$visJson}"""
	}
	val propAccessors = userProps.flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	val propsList = userProps.filter { emitsGet(it) }.joinToString(",") { p ->
		val setName = if (emitsSet(p)) str("set_" + p.name.asString()) else "null"
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()},"get":${str("get_" + p.name.asString())},"set":$setName}"""
	}
	val setThis = { f: String, v: String -> """{"k":"setField","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":${str(f)},"value":$v}""" }
	val loc = { n: String -> """{"k":"local","name":${str(n)}}""" }
	// ctor(__name, __ordinal, <user params>) storing each into a field.
	val ctorParams = (listOf("""{"name":"__name","type":${fqnJson("kotlin.String")}}""", """{"name":"__ordinal","type":${fqnJson("kotlin.Int")}}""") +
		userParams.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
	val ctorBody = (listOf(setThis("__name", loc("__name")), setThis("__ordinal", loc("__ordinal"))) +
		userParams.map { setThis(it.name.asString(), loc(it.name.asString())) }).joinToString(",")
	// Per-entry bodies (`PLUS { override fun apply(…)=… }`): the base enum is abstract with abstract members, and
	// each such entry is its own subclass overriding them. Detect them + the abstract members. (T A-109.)
	val hasPerEntry = entries.any { it.correspondingClass != null }
	val absMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { it.correspondingPropertySymbol == null && it.body == null && it.modality == Modality.ABSTRACT }
	val baseAbstract = hasPerEntry || absMethods.isNotEmpty()
	// base ctor must be callable from the entry subclasses -> protected (was private for the flat form).
	val ctor = """{"params":[$ctorParams],"baseArgs":null,"thisArgs":null,"vis":${str(if (hasPerEntry) "protected" else "private")},"body":[$ctorBody]}"""
	// instance fields: metadata + user props.
	val fields = (listOf("""{"name":"__name","type":${fqnJson("kotlin.String")}}""", """{"name":"__ordinal","type":${fqnJson("kotlin.Int")}}""") + userFields).toMutableList()
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
			subDefs.add(enumEntrySubclass(sub, name, cc, enumSuperArgs(cc)))
			fields.add("""{"name":${str(ent.name.asString())},"type":${fqnJson(name)},"static":true,"init":{"k":"new","type":${fqnJson(sub)},"args":[${nameOrd(i, ent).joinToString(",")}]}}""")
		} else {
			val ecc = (ent.initializerExpression as? IrExpressionBody)?.expression as? IrEnumConstructorCall
			val entryArgs = ecc?.let { regularArgs(it).map { a -> expr(a) } }.orEmpty()
			val newArgs = (nameOrd(i, ent) + entryArgs).joinToString(",")
			fields.add("""{"name":${str(ent.name.asString())},"type":${fqnJson(name)},"static":true,"init":{"k":"new","type":${fqnJson(name)},"args":[$newArgs]}}""")
		}
	}
	// methods: concrete user methods + abstract member decls + toString + values() + valueOf().
	val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
		.map { method(it, static = false) } +
		absMethods.map { m -> """{"name":${str(m.name.asString())},"static":false,"override":false,"virtual":true,"abstract":true,"vis":"public","params":[${paramsJsonList(m.parameters).joinToString(",")}],"ret":${birType(m.returnType).toJson()},"body":[]}""" }
	val sf = { e: IrEnumEntry -> """{"k":"staticField","ownerType":${fqnJson(name)},"name":${str(e.name.asString())}}""" }
	val toStr = """{"name":"toString","static":false,"override":true,"virtual":true,"objectOverride":true,"vis":"public","params":[],"ret":${fqnJson("kotlin.String")},"body":[{"k":"return","value":{"k":"field","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":"__name"}}]}"""
	val valuesArr = """{"k":"newArray","elem":${fqnJson(name)},"elems":[${entries.joinToString(",") { sf(it) }}]}"""
	val valuesM = """{"name":"values","static":true,"override":false,"virtual":false,"vis":"public","params":[],"ret":${TypeNode.Array(TypeNode.Fqn(name)).toJson()},"body":[{"k":"return","value":$valuesArr}]}"""
	val voBranches = entries.joinToString(",") { ent ->
		"""{"cond":{"k":"objEq","lhs":{"k":"local","name":"name"},"rhs":{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(ent.name.asString())}}},"body":[{"k":"return","value":${sf(ent)}}]}"""
	}
	// Kotlin's `Enum.valueOf` throws IllegalArgumentException on an unknown name (@ClrTypeAlias System.ArgumentException).
	val voThrow = throwExpr(newExc("kotlin.IllegalArgumentException", str("No enum constant $name")))
	val voBody = """{"k":"if","branches":[$voBranches,{"else":true,"body":[{"k":"exprStmt","expr":$voThrow}]}]}"""
	val valueOfM = """{"name":"valueOf","static":true,"override":false,"virtual":false,"vis":"public","params":[{"name":"name","type":${fqnJson("kotlin.String")}}],"ret":${fqnJson(name)},"body":[$voBody]}"""
	val methods = (userMethods + propAccessors + listOf(toStr, valuesM, valueOfM)).joinToString(",")
	// `enumRich:true` — a FAITHFUL "this class originated from a Kotlin enum" fact (not a CLR-shape decision), so
	// bir2cir's EnumIntrinsicLowering can lower `enumValues<ThisEnum>()` to the synthesized static values()/valueOf()
	// rather than the System.Enum-reflection semantic node (a rich enum is a plain class, invisible to that reflection).
	val baseDef = """{"name":${str(name)},"kind":"class","enumRich":true,"abstract":$baseAbstract,"vis":${str(visOf(ec))},"base":null,"interfaces":[],"fields":[${fields.joinToString(",")}],"ctors":[$ctor],"methods":[$methods],"properties":[$propsList]}"""
	// Emit the base enum class first, then each per-entry subclass.
	return (listOf(baseDef) + subDefs).joinToString(",")
}

/** The enum-super args a per-entry body's anonymous subclass passes (the `NAME(args)` args), as expr JSON. */
internal fun BirEmitter.enumSuperArgs(cc: IrClass): List<String> {
	val ctor = cc.declarations.filterIsInstance<IrConstructor>().firstOrNull() ?: return emptyList()
	val call = (ctor.body as? IrBlockBody)?.statements?.firstNotNullOfOrNull { it as? IrEnumConstructorCall }
		?: return emptyList()
	return regularArgs(call).map { expr(it) }
}

/** A per-entry enum body `NAME(args) { override fun … }` -> a subclass `<>Enum_NAME : Enum` whose ctor takes only
 *  (__name, __ordinal) and forwards them plus the baked-in `args` to the base ctor; carries the overriding methods. */
internal fun BirEmitter.enumEntrySubclass(subName: String, baseName: String, cc: IrClass, userArgs: List<String>): String {
	val overrides = cc.declarations.filterIsInstance<IrSimpleFunction>()
		.filter { it.body != null && it.correspondingPropertySymbol == null }
		.joinToString(",") { method(it, static = false) }
	val baseArgs = (listOf("""{"k":"local","name":"__name"}""", """{"k":"local","name":"__ordinal"}""") + userArgs).joinToString(",")
	val subCtor = """{"params":[{"name":"__name","type":${fqnJson("kotlin.String")}},{"name":"__ordinal","type":${fqnJson("kotlin.Int")}}],"baseArgs":[$baseArgs],"thisArgs":null,"vis":"public","body":[]}"""
	return """{"name":${str(subName)},"kind":"class","abstract":false,"vis":"public","base":${fqnJson(baseName)},"interfaces":[],"fields":[],"ctors":[$subCtor],"methods":[$overrides]}"""
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

internal fun BirEmitter.innerEnclosingTypeParams(klass: IrClass): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
	if (!klass.isInner) return emptyList()
	val result = mutableListOf<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	var p = klass.parent as? IrClass
	while (p != null) { result.addAll(0, p.typeParameters); p = if (p.isInner) p.parent as? IrClass else null }
	return result
}

internal fun BirEmitter.innerClassDef(inner: IrClass): String {
	val outerThis = (inner.parent as? IrClass)?.thisReceiver
		?: return typeDef(inner)   // not actually inner-of-class; emit plainly
	captureSubst[outerThis] = """{"k":"field","ownerType":${fqnJson(typeName(inner))},"recv":{"k":"this"},"name":"__outer"}"""
	val def = typeDef(inner, listOf(outerThis to "__outer"))
	captureSubst.remove(outerThis)
	return def
}

/** `@ClrField` opt-out: emit this property as a plain (public) CLR FIELD, no accessor/property. Detected by short
 *  name so any user-declared `ClrField` annotation triggers it. */
internal fun BirEmitter.isClrField(p: IrProperty): Boolean =
	p.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrField" }

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

/** STEP-1 (kotc->bir2cir clrName migration) — a PURE-KOTLIN override marker for an emitted member: the transitive
 *  closure of interface/base members it overrides, each as {owner FQN, Kotlin member name, kind, arity}. NO CLR
 *  knowledge (no @ClrIntrinsic read, no BCL name). bir2cir (Step 2) consumes this + the ref.dll @ClrIntrinsic to
 *  derive the BCL slot name. Behavior-neutral: bir2cir strips
 *  the `overrides` key, so it never reaches ilemit (Step 1 keeps CIR byte-identical). `member` is the property name
 *  for an accessor (kind getter/setter) so bir2cir can resolve `get_`/`set_` + the property's @ClrIntrinsic. */
internal fun BirEmitter.overridesJson(fn: IrSimpleFunction): String {
	val prop = fn.correspondingPropertySymbol?.owner
	val items = if (prop != null) {
		// An ACCESSOR: walk the PROPERTY's override closure (the setter of a `var size` overriding a `val size` has
		// NO own overriddenSymbols, but the PROPERTY overrides — so use the property chain, tagged with this accessor's
		// kind). bir2cir resolves get_/set_ + the property's @ClrIntrinsic (which lives on the get_<name> accessor).
		val kind = if (fn === prop.getter) "getter" else "setter"
		val ordered = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrProperty>()
		fun walkP(p: org.jetbrains.kotlin.ir.declarations.IrProperty) { for (ov in p.overriddenSymbols) { val o = ov.owner; if (ordered.add(o)) walkP(o) } }
		walkP(prop)
		ordered.mapNotNull { p -> (p.parent as? IrClass)?.fqNameWhenAvailable?.asString()?.let { owner ->
			"""{"owner":${fqnJson(owner)},"member":${str(p.name.asString())},"kind":${str(kind)},"arity":0}""" } }
	} else {
		val ordered = LinkedHashSet<IrSimpleFunction>()
		fun walk(f: IrSimpleFunction) { for (ov in f.overriddenSymbols) { val o = ov.owner; if (ordered.add(o)) walk(o) } }
		walk(fn)
		ordered.mapNotNull { m -> (m.parent as? IrClass)?.fqNameWhenAvailable?.asString()?.let { owner ->
			"""{"owner":${fqnJson(owner)},"member":${str(m.name.asString())},"kind":"method","arity":${m.parameters.count { it.kind == IrParameterKind.Regular }}}""" } }
	}
	return if (items.isEmpty()) "" else ""","overrides":[${items.joinToString(",")}]"""
}

/** A TOP-LEVEL property's accessor as a STATIC `get_<name>`/`set_<name>` method (extension receiver -> `__self`).
 *  Used for extension properties (`val T.p`) and computed top-level properties (no backing field). */
internal fun BirEmitter.topLevelAccessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
	val extRecv = acc.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
	// #6 non-null parameter PRECONDITIONS + getter return POSTCONDITION (gates on the accessor's REAL IR visibility, not
	// the hardcoded emitted "public" — a private top-level property's accessor is emitted public but is not the surface).
	val bodyStmts = withReturnPostcondition(acc) { (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	val body = (preconditionChecks(acc) + listOfNotNull(bodyStmts.takeIf { it.isNotEmpty() })).joinToString(",")
	if (extRecv != null) selfSubst.remove(extRecv)
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val ps = (listOfNotNull(selfParam) + paramsJsonList(acc.parameters)).joinToString(",")
	val name = (if (isGetter) "get_" else "set_") + propName
	val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
	return """{"name":${str(name)},"static":true,"override":false,"virtual":false,"abstract":false,"objectOverride":false,"vis":"public"${typeParamsJson(acc.typeParameters)},"params":[$ps],"ret":${str(ret)}${funModsJson(acc)},"body":[$body]}"""
}

internal fun BirEmitter.accessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
	val mname = (if (isGetter) "get_" else "set_") + propName
	// A MEMBER extension property (`class C { val T.p get() }`) has BOTH a dispatch and an extension receiver -> the
	// extension receiver rides a leading `__self` param (mirrors a member extension function); body refs to it
	// resolve via selfSubst (by identity, so it isn't confused with the dispatch `<this>`).
	val extRecv = acc.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
	if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val ps = (listOfNotNull(selfParam) + acc.parameters.filter { it.kind == IrParameterKind.Regular }
		.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
	// #6 non-null parameter PRECONDITIONS (a setter's `value` param) at entry + a getter's non-null return POSTCONDITION
	// (a setter returns Unit -> naturally out of scope).
	val bodyStmts = withReturnPostcondition(acc) { (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } }
	val body = (preconditionChecks(acc) + listOfNotNull(bodyStmts.takeIf { it.isNotEmpty() })).joinToString(",")
	if (extRecv != null) selfSubst.remove(extRecv)
	val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
	// An `override val/var` whose accessor overrides a base CLASS/ENUM_CLASS accessor must REUSE that base virtual
	// slot (`override`, not a fresh NewSlot) — EXACTLY like an overriding method (see method()'s `isOverride`).
	// Otherwise a concrete subclass leaves the base's abstract accessor slot unfilled -> TypeLoadException at load
	// ("get_X ... does not have an implementation"). This mirrors method() so property accessors and methods agree.
	// Interface members bind by name/signature (ilemit's DefineMethodOverride pass) so they don't need this flag;
	// use the accessor's OWN overriddenSymbols (a setter that ADDS to a base `val` has none -> stays a NewSlot).
	val isOverrideClass = acc.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
	val virtual = acc.modality == Modality.OPEN || acc.modality == Modality.ABSTRACT || acc.overriddenSymbols.isNotEmpty()
	val vis = visOf(acc)
	val isAbstract = acc.modality == Modality.ABSTRACT && acc.body == null
	// Emit the PROPERTY's annotations (e.g. @ClrIntrinsic) onto its accessor method — the SAME unconditional
	// pass-through method()/ifaceMethod already do for plain methods (kotc does not filter/select annotations;
	// attrsJson doctrine). The @ClrIntrinsic is on the property (`@ClrIntrinsic("Length") val length`), so read it
	// from the corresponding property. bir2cir consumes it from the get_<name> accessor (TryMemberIntrinsic /
	// DeclarationRename) to lower a `.length` read to clrPropGet Length. In a stdlib build the ref.dll carries the
	// binding; the rt build strips ALL metadata downstream (ilemit under `--build-stdlib=runtime`) so the rt.dll
	// never carries it. In an app build these attrs simply ride the accessor as ordinary metadata.
	val propAnns = (acc.correspondingPropertySymbol?.owner ?: acc).annotations
	val accAttrs = ""","attrs":[${attrsJson(propAnns)}]"""
	return """{"name":${str(mname)},"static":false,"override":$isOverrideClass,"virtual":$virtual,"abstract":$isAbstract,"objectOverride":false,"vis":${str(vis)},"params":[$ps],"ret":${str(ret)}${funModsJson(acc)},"body":[$body]$accAttrs${overridesJson(acc)}}"""
}

/** A user `annotation class Ann(val v: Int, …)` -> a plain BIR class carrying the pure-Kotlin `"annotation":true`
 *  FLAG (ctor params -> public fields). "This is an annotation" is a Kotlin-language fact; "annotations extend
 *  System.Attribute on the CLR" is the Kotlin<->CLR relation, so kotc emits ONLY the flag (base:null) and
 *  bir2cir DERIVES `base = System.Attribute` from it (annotation-base-lowering-to-bir2cir, USER 2026-07-02).
 *  kotc names NO CLR base type here. */
internal fun BirEmitter.annotationDef(klass: IrClass): String {
	val ctorParams = klass.declarations.filterIsInstance<IrConstructor>().firstOrNull { it.isPrimary }
		?.parameters?.filter { it.kind == IrParameterKind.Regular }.orEmpty()
	val fields = ctorParams.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
	val assigns = ctorParams.joinToString(",") { """{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(it.name.asString())},"value":{"k":"local","name":${str(it.name.asString())}}}""" }
	val ctor = """{"params":[$fields],"baseArgs":[],"thisArgs":null,"vis":"public","body":[$assigns]}"""
	return """{"name":${str(typeName(klass))},"kind":"class"${classModsJson(annotation = true)},"abstract":false,"vis":"public","base":null,"interfaces":[],"fields":[$fields],"ctors":[$ctor],"methods":[]}"""
}

/** The `attrs` JSON for a declaration: each annotation -> a .NET custom attribute application. The `attr` type is a
 *  structured `{t:fqn}` identity node (#48). A Kotlin-authored annotation is named by its plain Kotlin FQN (#46) —
 *  bir2cir derives its `: System.Attribute` base from the `"annotation":true` flag on the class def. An imported .NET
 *  attribute (a facadegen-injected annotation class) is named by its real .NET FQN and flagged `"attrClr":true` (a
 *  frontend origin fact — kotc KNOWS the type was injected, via clrName); bir2cir consumes that flag into the
 *  `attrExternal` bit so ilemit binds the existing .NET constructor (#54/#48). kotc emits no `clr:` marker.
 *
 *  kotc does NOT filter/select annotations: from kotc's view an annotation is just METADATA, so EVERY annotation is
 *  passed through to the BIR verbatim (incl. @ClrTypeAlias, @ClrIntrinsic, and every other `kotlin.*` annotation).
 *  The ref.dll consumer (bir2cir) is the CLR layer that decides what to do with each attribute. (The old keep-list —
 *  drop `kotlin.*` except @ClrIntrinsic/@ClrIntrinsicAsDynamic — was a kotc-side SELECT and is removed: a
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
	if (captureEnclosingGenerics) {
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
				if ((subst == null || containsTv(subst)) && cls.owner.name.asString() !in excluded)
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
	// A super-typed companion is emitted as a separate lifted singleton (<Outer>.InstanceClass), NOT flattened into
	// this (often abstract) parent's statics. Only a plain companion (no supertype) flattens here.
	val companion = if (superTypedCompanion(klass) != null) null
		else klass.declarations.filterIsInstance<IrClass>().firstOrNull { it.isCompanion }
	// #187: a class DIRECTLY implementing a .NET interface event must `override val E by clrEvent()` (else invalid type).
	checkUnimplementedClrEvents(klass)
	// `override val E by clrEvent()` synthesis (§4.2): the field-like event impl (backing directive + add_/remove_/raise_).
	val (clrEventBackings, clrEventMethods) = synthClrEvents(klass)
	val instFields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { p ->
		// A `by clrEvent()` property's `<E>$delegate` backing field (of the un-emittable `kotlin.clr.ClrEvent<T>` type) is
		// REPLACED by the synthesized backing delegate field (bir2cir stamps `<E>$delegate : D`); never emit the fiction.
		if (clrEventDelegateCall(p) != null) return@mapNotNull null
		val bf = p.backingField ?: return@mapNotNull null
		// Honor the property's visibility on its backing field (A-108): a `private`/`internal`/`protected`
		// property gets a non-public field. (Kotlin's own access rules already keep same-class field reads valid.)
		// An accessor-routed property's backing field is INTERNAL (assembly-visible): access goes through get_/set_
		// (CLR property model), yet it stays reachable IN-MODULE so a `byref(obj.prop)` can ldflda it (Phase 5) while a
		// cross-assembly consumer sees only the property. Only @ClrField / const / lateinit / delegated keep a plain field.
		val routed = p.getter != null && !p.isConst && !p.isLateinit && !p.isDelegated && !isClrField(p)
		val v = if (routed) "internal" else visOf(p); val visJson = if (v != "public") ""","vis":${str(v)}""" else ""
		// A property that isn't publicly SETTABLE (`val`, or `var ... private/protected set`) -> mark the public
		// backing field read-only so a consuming Kotlin module restores it as `val` (rejecting external writes).
		val ro = if (!routed && (!p.isVar || (p.setter != null && visOf(p.setter!!) != "public"))) ""","readOnly":true""" else ""
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()}$visJson$ro${volatileFieldFlag(p)}}"""
	}
	// Standalone (non-property) instance fields the FRONTEND synthesized — chiefly the class-delegation backing
	// field `$$delegate_0` (origin DELEGATE) for `class Foo : Bar by baz`: an IrField with NO corresponding
	// property whose forwarding members (`DELEGATED_MEMBER`) read it via GET_FIELD. It carries an EXPRESSION_BODY
	// initializer (the delegate expression, usually a ctor param) run through the IrInstanceInitializerCall path
	// in `ctor` below, exactly like a property backing field. Emit it as a plain instance field so those reads resolve.
	val synthFields = klass.declarations.filterIsInstance<IrField>()
		.filter { it.correspondingPropertySymbol == null && !it.isStatic }
		.map { f ->
			val v = visOf(f); val visJson = if (v != "public") ""","vis":${str(v)}""" else ""
			val ro = if (f.isFinal) ""","readOnly":true""" else ""
			"""{"name":${str(f.name.asString())},"type":${birType(f.type).toJson()}$visJson$ro}"""
		}
	// Companion non-const `val`/`var` -> static fields (with initializer run in a static ctor); const is inlined.
	val statFields = companion?.declarations?.filterIsInstance<IrProperty>()?.mapNotNull { p ->
		val bf = p.backingField ?: return@mapNotNull null
		if (p.isConst) return@mapNotNull null
		val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
		"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()},"static":true,"init":$init${volatileFieldFlag(p)}}"""
	}.orEmpty()
	// A capturing object literal carries its captured outer values as extra instance fields.
	val capFields = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	// `object` singleton: a static `INSTANCE` field initialized to `new Foo()` (run in the .cctor) — same shape
	// as an enum entry. `IrGetObjectValue` loads it; member access then routes as normal instance access.
	val instanceField = if (isObject)
		listOf("""{"name":"INSTANCE","type":${fqnJson(typeName(klass))},"static":true,"init":{"k":"new","type":${fqnJson(typeName(klass))},"args":[]}}""")
	else emptyList()
	val fields = (instFields + synthFields + statFields + capFields + instanceField).joinToString(",")
	val ctors = klass.declarations.filterIsInstance<IrConstructor>().joinToString(",") { ctor(klass, it, captures) }
	val instMethods = klass.declarations.filterIsInstance<IrSimpleFunction>()
		// Include `abstract fun`s (body == null): they emit as CLR abstract methods so subclass overrides bind
		// and a base-typed call (`shape.area()`) resolves to the slot.
		.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && (it.body != null || it.modality == Modality.ABSTRACT) }
		.map { method(it, static = false) }
	// Companion methods -> static methods of the enclosing class.
	val statMethods = companion?.declarations?.filterIsInstance<IrSimpleFunction>()
		?.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && it.body != null }
		?.map { method(it, static = true) }.orEmpty()
	// A companion property's CUSTOM accessor -> a STATIC get_/set_<name> method on the enclosing class. Emitted
	// only when the accessor is CUSTOM (not the trivial `field` passthrough): covers a no-backing-field computed
	// property AND a backing-field property with a custom `get()/set()` (`val kProp = 7; get() = field + 100`,
	// #89), whose read/write must route through the accessor instead of a raw static-field load. Getter and
	// setter are decided independently (a `var` may pair a default getter with a custom setter).
	val companionAccessors = companion?.declarations?.filterIsInstance<IrProperty>()
		?.flatMap { p ->
			listOfNotNull(
				p.getter?.takeIf { fieldRoutedProperty(p) && !hasDefaultGetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
				p.setter?.takeIf { fieldRoutedProperty(p) && !hasDefaultSetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
		}.orEmpty()
	// User custom accessors (`get() = …`/`set(v){…}`) -> get_/set_ methods (the access site routes through them).
	// A property optimizes to a plain field; but one implementing a KOTLIN INTERFACE property must emit a get_/set_
	// METHOD to bind the interface slot (property-accessor analog of the method-side overridesIface fix; e.g.
	// ComparableRange.start over ClosedRange.start). See design-clr-property-model.md. This is ALSO the sole producer
	// of accessors that OVERRIDE a .NET base-CLASS virtual property (`override val Message` over System.Exception):
	// accessorMethod emits the plain get_/set_ + the `overrides` marker, and bir2cir's DeclarationRename derives the
	// `clrOverride` field from that marker (kotc emits no clrOverride — A2 / #73 M4.3).
	fun ovIface(a: IrSimpleFunction) = a.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.INTERFACE }
	// A FAKE-OVERRIDE property whose implementation is INHERITED FROM A BASE CLASS (`name` in `Sq : Shape("sq")`)
	// or from a CLR default interface property has accessors with NO body. Emitting such an accessor creates a new,
	// empty override (returning the CLR default value) and shadows the inherited implementation. An ABSTRACT
	// fake-override resolved only to an INTERFACE member (AbstractMutableList.size over MutableList.size) is KEPT:
	// the CLR requires the (abstract) class to re-declare the unimplemented interface slot.
	fun implementationInherited(a: IrSimpleFunction?): Boolean {
		val resolved = a?.resolveFakeOverride() ?: return false
		return (resolved.parent as? IrClass)?.kind == ClassKind.CLASS || resolved.modality != Modality.ABSTRACT
	}
	fun dropFake(p: IrProperty) = p.isFakeOverride && implementationInherited(p.getter)
	// `!isClrEventProperty`: a `kotlin.clr.ClrEvent<T>` fake-override (a .NET event inherited through a base's
	// interface) is not a real property and must not surface an accessor/property member.
	fun emitsGet(p: IrProperty) = p.getter != null && !p.isConst && !p.isLateinit && !p.isDelegated && !isClrField(p) && !dropFake(p) && !isClrEventProperty(p)
	fun emitsSet(p: IrProperty) = p.setter != null && !p.isConst && !p.isLateinit && !p.isDelegated && !isClrField(p) && !dropFake(p) && !isClrEventProperty(p)
	val userAccessors = klass.declarations.filterIsInstance<IrProperty>().flatMap { p ->
		listOfNotNull(
			p.getter?.takeIf { emitsGet(p) }?.let { accessorMethod(it, p.name.asString(), true) },
			p.setter?.takeIf { emitsSet(p) }?.let { accessorMethod(it, p.name.asString(), false) })
	}
	// Real CLR properties: a `properties` entry per accessor-bearing property -> ilemit DefineProperty's it over
	// the get_/set_ methods, so a C#/reflection consumer sees a property. (Full "every property -> CLR property +
	// @ClrField opt-out" is the next phase; field-backed props keep their backing field for now.)
	val propsList = klass.declarations.filterIsInstance<IrProperty>().filter { emitsGet(it) }.joinToString(",") { p ->
		val getName = "get_" + p.name.asString()
		val setName = if (emitsSet(p)) str("set_" + p.name.asString()) else "null"
		"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()},"get":${str(getName)},"set":$setName${overridesJson(p.getter!!)}}"""
	}
	val methods = (instMethods + statMethods + companionAccessors + userAccessors + clrEventMethods).joinToString(",")
	// A .NET base class (`: System.Exception(...)`, incl. a generic `: Collection<Int>()`) -> a `clr:`/`clrg:`
	// type spec (via birType) that ilemit resolves by reflection; a Kotlin-user base emits its bare FQN identity
	// carrying its ACTUAL constructed type arguments.
	val baseJson = base?.let {
		// A .NET-injected base carries its full constructed identity (birType). A Kotlin-user/stdlib base emits its
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
	val ifaces = klass.superTypes
		.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
		.mapNotNull { st ->
			// A stdlib interface birType maps to .NET (Continuation, Comparable, Comparator, AutoCloseable, …) ->
			// its clr:/clrg: spec; a user generic interface `Container<Int>` -> the constructed spec `Container[int]`
			// (ownerSpec). The Kotlin iterator/iterable protocol is NOT special-cased: a user `class R : Iterable<Int>`
			// links the REAL generic `kotlin.collections.Iterable<Int>` (bir2cir @ClrTypeAlias'd to
			// `IEnumerable<int>`; ilemit's reverse bridge synthesizes `GetEnumerator` from the class's `iterator()`),
			// and an `Iterator<Int>` supertype the real generic `kotlin.collections.Iterator<Int>` (a real emitted
			// stdlib interface). `for (x in r)` resolves the iterator on that real generic — the real generic
			// interface is used, and every build takes the same reverse-bridge path.
			val bt = birType(st)
			val stClass = st.classifierOrNull?.owner as? IrClass
			when {
				bt is TypeNode.Fn -> null
				stClass != null && isExternalNetType(stClass) -> bt.toJson()
				else -> stClass?.let { ownerSpec(it, st).toJson() }
			}
		}
		.joinToString(",")
	// Anonymous objects (lifted, tracked in anonNames) are synthetic -> keep public.
	val vis = if (anonNames.containsKey(klass)) "public" else visOf(klass)
	val isAbstract = klass.modality == Modality.ABSTRACT || klass.modality == Modality.SEALED
	// A `nested`/`inner` class is emitted as a true CLR nested type of its enclosing user class (`Outer+Inner`),
	// so it retains Kotlin's access to the enclosing class's private members (instead of flattening to a separate
	// top-level type, which forced an assembly-visibility workaround). `inner` additionally captures `__outer`.
	// EXCEPTION: a type nested in a GENERIC enclosing flattens to top-level — PersistedAssemblyBuilder NREs when a
	// nested type lives inside a generic enclosing TypeBuilder, and the nested type's signatures reference the
	// enclosing params via the enclosing builder. Inner classes already re-declare those params (innerEnclosing-
	// TypeParams), so flattening loses nothing; the type keeps its dotted name so references still resolve.
	val nestedIn = emittedNestedParent(klass)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && !isExternalNetType(it) && !anonNames.containsKey(klass) && it.typeParameters.isEmpty() }
		?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
	// Round-trip: a Kotlin `sealed` class lowers to a CLR abstract class (loses the sealed modality) — carry the fact
	// so a re-consuming Kotlin module restores `sealed` (ilemit stamps [KotlinSealed]). `value` (inline class) is
	// likewise carried as a mod — the 2.4.0 frontend no longer surfaces its `@JvmInline` annotation, so this modifier
	// is bir2cir's sole value-class signal for the erase-to-underlying lowering (see classModsJson).
	val sealedFlag = classModsJson(
		sealed = klass.modality == Modality.SEALED,
		value = klass.isValue,
		objectSingleton = isObject
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
	// `clrEvents` (§4.2): per-event backing directives for the `by clrEvent()` synthesis — bir2cir's ClrEventImplBinding
	// turns each into a real `<E>$delegate : D` field + a type-level `clrEventDecl` (the `.event` metadata record).
	val clrEventsJson = if (clrEventBackings.isEmpty()) "" else ""","clrEvents":[${clrEventBackings.joinToString(",")}]"""
	val result = """{"name":${str(typeName(klass))},"kind":"class","abstract":$isAbstract,"vis":${str(vis)}$nestedIn$sealedFlag$tpJson$generatedFlag,"base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods],"properties":[$propsList]$clrEventsJson,"attrs":[${attrsJson(klass.annotations)}]${posJson(klass)}}"""
	// Restore the captured-param remap installed at the top.
	savedCaptureSubst.forEach { (tp, prev) -> if (prev != null) typeArgSubst[tp] = prev else typeArgSubst.remove(tp) }
	return result
}

internal fun BirEmitter.ctor(klass: IrClass, ctor: IrConstructor, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
	// Captured outer values arrive as leading ctor params and are stored into the capture fields first
	// (the instance initializers below read them, e.g. `var cur = from` -> `this.__outer.from`).
	val capParams = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
	val capAssigns = captures.map { (_, fname) ->
		"""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}"""
	}
	val params = (capParams + paramsJsonList(ctor.parameters)).joinToString(",")
	val body = ctor.body as? IrBlockBody
	val delegating = body?.statements?.filterIsInstance<IrDelegatingConstructorCall>()?.firstOrNull()
	val delegateClass = delegating?.symbol?.owner?.parent as? IrClass
	// `constructor(...) : this(...)` delegates to a sibling ctor; `: super(...)` / implicit -> base.
	val isThisDelegate = delegating != null && delegateClass == klass
	val thisArgs = if (isThisDelegate) delegating!!.arguments.filterNotNull().joinToString(",") { expr(it) } else null
	val baseArgs = if (!isThisDelegate) delegating?.let { d ->
		val targetFq = delegateClass?.fqNameWhenAvailable?.asString()
		if (targetFq == "kotlin.Any") null else {
			// A CAPTURING local base (`open class A { fun go() { n++ } }` + `class B : A()`) took its captures as
			// leading ctor params when it was lifted, so this delegation must supply them ahead of the source-level
			// arguments — the construction-site rule, one level up the hierarchy. THIS class captures them too (the
			// capture scan follows the delegation), so each is already a leading param of the ctor we are emitting;
			// pass that param, not `capValueExpr`, because the capture FIELDS are only assigned after the base call.
			val baseCaps = delegateClass?.let { localClassCaptures[it] }.orEmpty().map { decl ->
				val here = captures.firstOrNull { (d, _) -> d === decl }?.second
					?: return@let invariantBroken(d,
						"a local base class's capture is not a capture of the derived local class")
				"""{"k":"local","name":${str(here)}}"""
			}
			(baseCaps + d.arguments.filterNotNull().map { expr(it) }).joinToString(",")
		}
	} else null
	val stmts = ArrayList<String>()
	stmts.addAll(capAssigns)   // store captures before instance initializers, which may read them
	body?.statements?.forEach { s ->
		when (s) {
			is IrDelegatingConstructorCall -> {}
			is IrInstanceInitializerCall -> klass.declarations.forEach { d ->
				when (d) {
					// A `by clrEvent()` property has NO backing `<E>$delegate` field (synthClrEvents replaced it with the
					// event's own `<E>$delegate : D` field, initialized to null); skip its ctor delegate-store, which would
					// otherwise call the erased `clrEvent()` marker + store into the removed field.
					is IrProperty -> if (clrEventDelegateCall(d) == null) d.backingField?.let { bf -> bf.initializer?.let {
						// Use the backing-field name (a delegated property's field is `<name>$delegate`).
						stmts.add("""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(bf.name.asString())},"value":${expr((it as IrExpressionBody).expression)}}""")
					} }
					// A standalone synthetic field (class-delegation `$$delegate_0`) initializes here too: its
					// EXPRESSION_BODY (the delegate expr — typically the ctor param) stores into the field, exactly
					// like a property backing field. Static synthetic fields run in the .cctor, not here.
					is IrField -> if (d.correspondingPropertySymbol == null && !d.isStatic) d.initializer?.let {
						stmts.add("""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(d.name.asString())},"value":${expr((it as IrExpressionBody).expression)}}""")
					}
					is IrAnonymousInitializer -> (d.body as? IrBlockBody)?.statements?.forEach { stmts.add(stmt(it)) }
					else -> {}
				}
			}
			else -> stmts.add(stmt(s))
		}
	}
	val baseJson = baseArgs?.let { "[$it]" } ?: "null"
	val thisJson = thisArgs?.let { "[$it]" } ?: "null"
	// #6 non-null parameter PRECONDITIONS at entry. They land AFTER the base/`this` ctor delegation (baseArgs/thisArgs
	// ride a separate field), so a null user param dereferenced by a base-ctor arg NREs before this friendly NPE — an
	// accepted ordering deviation from JVM's before-super() insertion (docs/dotkt-semantics.md).
	val ctorBody = (preconditionChecks(ctor) + stmts).joinToString(",")
	return """{"params":[$params],"baseArgs":$baseJson,"thisArgs":$thisJson,"vis":${str(visOf(ctor))},"body":[$ctorBody]}"""
}

internal fun BirEmitter.method(fn: IrSimpleFunction, static: Boolean): String {
	// An override of a CLASS or ENUM_CLASS member (the latter: a per-entry enum body overriding an abstract enum
	// member) reuses the base virtual slot. (Interface members bind by name/signature, handled elsewhere.)
	val isOverride = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
	// A method that implements/overrides a Kotlin INTERFACE member must be virtual on the CLR to bind the interface
	// slot — even when it is Kotlin-`final` (final override -> CLR `virtual final` = sealed). Otherwise the type
	// fails to load with "must be virtual to implement a method on an interface or super type" (e.g. Enum.compareTo,
	// the primitive Iterator.next).
	val overridesIface = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.INTERFACE }
	val isVirtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT || overridesIface
	// An extension function `fun T.f()` -> static method whose first param `__self` is the receiver;
	// body references to the receiver resolve to `__self` (via valSubst).
	val extRecv = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
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
	// `override fun toString()/equals()/hashCode()` emits the KOTLIN name + `objectOverride:true` (a pure-Kotlin
	// fact); bir2cir/ilemit map it onto the System.Object slot so CLR virtual dispatch (Console.WriteLine,
	// structural `==`) finds the override.
	val isAnySlot = isAnySlotMethod(fn)
	val emitName = fn.name.asString()
	val isOvr = isOverride || isAnySlot
	// Object-overrides / interface members must stay public for virtual dispatch.
	// A PRIVATE TOP-LEVEL fun is FILE-private in Kotlin, but kotc's emission splits a file across CLR types
	// (the XKt file class + the file's classes), so CLR `private` under-approximates it: a same-file class
	// calling the helper threw MethodAccessException at run (Duration..cctor -> DurationKt.durationOfMillis).
	// Emit `internal` — the tightest CLR visibility that preserves same-file access (the same reasoning that
	// makes routed property backing fields internal). Class members keep their real visibility.
	val vis = if (isAnySlot) "public"
		else visOf(fn).let { if (it == "private" && fn.parent is org.jetbrains.kotlin.ir.declarations.IrPackageFragment) "internal" else it }
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
	return """{"name":${str(emitName)},"static":$static,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${isAnySlot},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}$mods${resultTypeJson(fn)},"params":[$ps],"ret":${birType(fn.returnType).toJson()},"body":[$body],"attrs":[${attrsJson(fn.annotations)}]${overridesJson(fn)}${posJson(fn)}}"""
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
	objectSingleton: Boolean = false
): String {
	val flags = buildList {
		if (annotation) add(""""annotation":true""")
		if (fnIface) add(""""fun":true""")
		if (sealed) add(""""sealed":true""")
		if (value) add(""""value":true""")
		if (objectSingleton) add(""""object":true""")
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
// bakes NO coroutine ABI. `isAwaitIntrinsic` is the ONLY coroutine helper left in kotc — it skips the await
// intrinsic method from emission (the suspend-call tag itself is emitted by `suspendCallTag` on the call node).
internal fun BirEmitter.isAwaitIntrinsic(fn: IrSimpleFunction): Boolean =
	fn.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrAwait" }

/**
 * `,"typeParams":[...]` for a generic class/interface/method (empty when non-generic). An unconstrained param
 * is a bare name string `"T"`; a bounded one (`<T : Comparable<T>>`) is `{"name":"T","constraints":[...]}`
 * (each constraint a BIR type, e.g. `clrg:System.IComparable[gp:T]`). `kotlin.Any` bounds are dropped.
 */
internal fun BirEmitter.typeParamsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String {
	if (tps.isEmpty()) return ""
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
		if (bounds.isEmpty() && variance == null) str(tp.name.asString())
		else {
			val parts = ArrayList<String>()
			parts.add(""""name":${str(tp.name.asString())}""")
			if (bounds.isNotEmpty()) parts.add(""""constraints":[${bounds.joinToString(",") { str(it) }}]""")
			if (variance != null) parts.add(""""variance":${str(variance)}""")
			"{${parts.joinToString(",")}}"
		}
	}
	return ""","typeParams":[$entries]"""
}

internal fun BirEmitter.paramsJson(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): String =
	paramsJsonList(params).joinToString(",")

internal fun BirEmitter.isValueParameter(p: IrValueParameter): Boolean =
	p.kind == IrParameterKind.Regular || p.kind == IrParameterKind.Context

/** THE 2-TIER default-argument test (docs/dotkt-semantics.md): can the parameter's OWN CLR type carry its default as
 *  a `[DefaultParameterValue]` constant? TRUE (Tier 1) — a primitive/char/bool const on its primitive param, a String
 *  const on a `String` param, or a null const on any nullable/reference param → native `[Optional]`+`[DefaultParameterValue]`.
 *  FALSE (Tier 2) — a String const on a NON-String reference param (`CharSequence`: a string constant cannot sit on an
 *  interface-typed param), or ANY non-constant default → `@KotlinDefault(bir)` + a REQUIRED param (a kcc consumer
 *  splices the expression, a C# consumer passes the arg explicitly). */
internal fun BirEmitter.isMetadataRepresentableDefault(p: org.jetbrains.kotlin.ir.declarations.IrValueParameter): Boolean {
	val def = p.defaultValue?.expression as? org.jetbrains.kotlin.ir.expressions.IrConst ?: return false
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
 *  This MUST cover Tier-1 too: at a CROSS-MODULE call kotc sees the callee's default as an IrErrorExpression (the
 *  frontend KLIB drops the VALUE) and so cannot tell Tier-1 from Tier-2 — it emits a `defaultArg` placeholder for EVERY
 *  omitted default, which bir2cir can only fill if a `@KotlinDefault` exists for that slot. (Tier-1 params ALSO keep the
 *  native `[Optional]` + `[DefaultParameterValue]` metadata for a C#/VB/F# consumer — unchanged; `@KotlinDefault` is the
 *  kcc-consumer splice source, ref.dll-only, stripped from the runtime build.)
 *
 *  Coverage: a top-level / extension NON-suspend fn (the original cross-module scope, static-emitted), OR ANY `inline`
 *  fn. The inline branch is the #34 residual: a MEMBER or suspend `inline` fn's omitted non-const default is read by
 *  InlineSplice STEP 5 from THIS carrier (previously a member/suspend fn carried nothing → InlineSplice fail-loud
 *  "missing (non-defaulted) arg", e.g. kotlinx.coroutines `BufferedChannel.sendImpl(... onNoWaiterSuspend = { ... })`).
 *  The carrier always rides the ref.dll param attribute (both consumers read it there — a cross-module member
 *  `callInstance` fills via DefaultArgSplice too), so the member/suspend expansion being gated to `inline` is NOT a
 *  mechanism limit but an EMPIRICAL firewall: carrying every NON-inline suspend coroutine decl regressed the runtime
 *  stdlib emit ("ilemit: cannot resolve .NET type kotlin.Unit"), root-cause unestablished, so a non-inline
 *  member/suspend fn stays uncarried for now (its cross-module default is a separate, pre-existing gap — a Tier-2
 *  default drops to a null/zero backfill; see docs/dotkt-semantics.md §10.2). The one genuinely-unsafe default SHAPE
 *  that a carrier CANNOT represent even for an inline fn — one that reads an enclosing-instance (dispatch or outer)
 *  receiver — is poisoned per-expression in [defaultCarrierBir] (a `{k:this}` dispatch read binds correctly ONLY in
 *  InlineSplice's `recv==dispatch` path, not for a member-EXTENSION splice nor DefaultArgSplice — since the SAME carrier
 *  feeds all consumers, it is refused rather than risk a miscompile). */
internal fun BirEmitter.carriesKotlinDefault(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
	fn.parameters.any { it.kind == IrParameterKind.Regular && it.defaultValue != null } &&
		(fn.isInline || (!fn.isSuspend && fn.parameters.none { it.kind == IrParameterKind.DispatchReceiver }))

/** A data-class `copy` synthetic — `copy` cannot be user-declared on a data class, so name + `isData` parent is exact. */
internal fun BirEmitter.isDataClassCopy(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
	fn.name.asString() == "copy" && (fn.parent as? IrClass)?.isData == true

/** #146: the @KotlinDefault carrier BIR for a default expression, made CLOSED so bir2cir can re-emit it at a
 *  cross-module omitted call site. A constant / simple call (`= emptyList()`) emits NO lifted method, so its BIR is
 *  carried verbatim (byte-identical to the #134 constant carrier). A NON-CAPTURING lambda default (`= {}`, the
 *  Avalonia `configure: Panel.() -> Unit = {}` idiom) lifts a `__lambdaN` static method into THIS file's `liftedMethods`
 *  and returns a `newDelegate` referencing it — an OPEN term (the method is library-local). Detach that lift DELTA from
 *  this library's file class (it is dead here — only the default's call sites materialize it) and wrap it with its
 *  `newDelegate` in a `defaultCarrier` envelope; bir2cir's DefaultArgSplice re-hoists the carried method app-local (fresh
 *  name) at the consumer. A CAPTURING closure / SAM / suspend lambda default cannot be positionally reconstructed
 *  cross-module → a `defaultUnsupported` poison carrier the consumer's splice refuses on (a precise diagnostic, not a
 *  miscompile). A default that reads `ownerFn`'s ENCLOSING-INSTANCE receiver — its own DISPATCH receiver (`this@Owner`)
 *  OR an OUTER class's `this@Outer` (inner-class member) — is EQUALLY poisoned: the carrier is consumed by BOTH
 *  InlineSplice (which binds a `{k:this}` to the dispatch temp only when `recv==dispatch` — NOT for a member-extension
 *  splice) AND DefaultArgSplice (which binds `{k:this}` to args[0] = the first regular arg on a `callInstance`, never an
 *  enclosing instance), so such a read cannot be filled safely from one uniform carrier. Detected by an IR-symbol scan
 *  of the dispatch-receiver param AND every enclosing class thisReceiver ([defaultReadsDispatch], NOT a `{k:this}`
 *  substring — a nested object/lambda `this` is a different receiver, and a pure-extension `this` is the extension
 *  receiver, which binds correctly to args[0]). */
internal fun BirEmitter.defaultCarrierBir(def: org.jetbrains.kotlin.ir.expressions.IrExpression,
		ownerFn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction? = null): String {
	if (ownerFn != null && defaultReadsDispatch(ownerFn, def))
		return """{"k":"defaultUnsupported","reason":${str("a default that reads an enclosing-instance (dispatch or outer-class) receiver cannot be filled at an omitting call site — pass the argument explicitly")}}"""
	val before = liftedMethods.size
	val bir = expr(def)
	val delta = if (liftedMethods.size > before) {
		val d = ArrayList(liftedMethods.subList(before, liftedMethods.size))
		while (liftedMethods.size > before) liftedMethods.removeAt(liftedMethods.size - 1)
		d
	} else emptyList()
	if (bir.contains(""""k":"newClosure"""") || bir.contains(""""k":"newSam"""") || bir.contains(""""k":"newSuspendLambda""""))
		return """{"k":"defaultUnsupported","reason":${str("a capturing / SAM / suspend lambda default cannot be filled at a cross-module call site — pass the argument explicitly")}}"""
	if (delta.isEmpty()) return bir
	return """{"k":"defaultCarrier","expr":$bir,"lifted":[${delta.joinToString(",")}]}"""
}

internal fun BirEmitter.paramsJsonList(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>,
		ownerFn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction? = null): List<String> {
	// A `@KotlinDefault(index, bir)` on each defaulted param of a qualifying function: `index` = the param's position
	// in the emitted call (extension receiver first, if any), `bir` = the default expression as a BIR-json STRING (so
	// bir2cir splices it PRE-lowering; it is opaque to this build's type lowering). Stamped on ALL defaulted params of
	// a Tier-2-carrying function (uniform splice source). kotc emits ONE BIR for every build: the attr rides every
	// build; the rt build strips it downstream (ilemit param-attr strip under `--build-stdlib=runtime`).
	val emitKotlinDefault = ownerFn != null && carriesKotlinDefault(ownerFn)
	val extOffset = if (ownerFn?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true) 1 else 0
	val valueParams = params.filter { isValueParameter(it) }
	// A @KotlinDefault BIR whose default expression reads ANOTHER value parameter (`b: Int = a * 10`) must encode that
	// read as a call-index token, NOT a bare `local a` (which would resolve to a non-existent local in the CALLER after
	// bir2cir's cross-module splice). Install a `{"k":"defaultArgParam","idx":N}` captureSubst for every value param
	// (N = its emitted call index, extension receiver counted first) around the bir emission; bir2cir's DefaultArgSplice
	// substitutes each token with this call's arg at that index (the peer of its `{this}` → receiver substitution).
	if (emitKotlinDefault) valueParams.forEachIndexed { regIdx, vp ->
		captureSubst[vp] = """{"k":"defaultArgParam","idx":${regIdx + extOffset}}"""
	}
	val result = valueParams
		.mapIndexed { regIdx, it ->
			// `vararg xs: T` -> mark the param so ilemit stamps [ParamArray] (native .NET varargs; a cross-module
			// consumer can then call `f(1, 2, 3)`). Param nullability now rides the `type` node itself
			// (`{t:nullable,of:...}` from the uniform birType) — the decl-level `nullable` flag is RETIRED.
			val vararg = if (it.varargElementType != null) ""","mods":{"vararg":true}""" else ""
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
				val bir = defaultCarrierBir(def, ownerFn)   // BIR of the default (real IR — the callee's own build), CLOSED for cross-module splice
				"""{"attr":${fqnJson("kotlin.clr.KotlinDefault")},"argTypes":[${fqnJson("kotlin.Int")},${fqnJson("kotlin.String")}],"args":[{"k":"const","type":${fqnJson("kotlin.Int")},"value":${regIdx + extOffset}},{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(bir)}}]}"""
			} else null
			val allAttrs = listOfNotNull(srcAttrs.takeIf { s -> s.isNotEmpty() }, kotlinDefault).joinToString(",")
			val pattrs = if (allAttrs.isNotEmpty()) ""","attrs":[$allAttrs]""" else ""
			"""{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}$vararg$default$pattrs}"""
		}
	if (emitKotlinDefault) valueParams.forEach { captureSubst.remove(it) }
	return result
}

/** A `,"sig":[<TypeNode>,...]` field carried on a call so ilemit resolves the right OVERLOAD by name+signature.
 *  Emit it ALWAYS: for a non-overloaded callee it's harmless (ilemit's `MethodsBySig` lookup hits the sole method,
 *  or falls back to the name), and emitting unconditionally avoids any overload-detection edge case. The signature
 *  MATCHES how `method()` lays out the def's `params` ([ext receiver?] + regular params, each `birType`) — the
 *  #37 m3b type-path structuring: sig is a STRUCTURED TypeNode array (the same `birType(...).toJson()` path every
 *  other type slot uses), NOT a legacy comma-string; bir2cir/ilemit derive the overload key from the TypeNodes. */
internal fun BirEmitter.overloadSigField(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String {
	val ext = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }?.let { birType(it.type) }
	val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { birType(it.type) }
	return ""","sig":[${(listOfNotNull(ext) + regs).joinToString(",") { it.toJson() }}]"""
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
	val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
	if (!hasDispatch || hasExt) return false
	val reg = fn.parameters.count { it.kind == IrParameterKind.Regular }
	return when (fn.name.asString()) {
		"toString", "hashCode" -> reg == 0
		"equals" -> reg == 1
		else -> false
	}
}
