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
		val body = if (hasDefault) (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) } else ""
		// A generic interface method (`fun <E> get(...)`, `<R> fold(...)`) must carry its own type params, else
		// `gp:E`/`gp:R` in its signature is unresolvable at emit (CoroutineContext / ContinuationInterceptor / …).
		// `attrs`: ride the @Clr/[Kotlin*] metadata so the ref assembly carries the BCL binding hint (for app-emit
		// substitution). For a PROPERTY accessor the binding is on the property (size @ClrIntrinsic("Count")), so read from there.
		val memberAttrs = attrsJson((prop ?: fn).annotations)
		// A `suspend fun` interface member carries the SAME neutral `"suspend":true`+`resultType` FACT the concrete
		// `method()` path emits (BirEmitter.kt:1413). Without it bir2cir has nothing to key off for an INTERFACE
		// suspend member — it can't synthesize the Task-bridge signature / cold-entry — so a cross-assembly
		// `interface Fetcher { suspend fun fetch(): Int }` round-trip breaks (the abstract-CLASS path already tags it).
		return """{"name":${str(name)},"static":false,"override":false,"virtual":true${typeParamsJson(fn.typeParameters)},"params":[${paramsJson(fn.parameters)}],"ret":${str(ret)}${funModsJson(fn)}${resultTypeJson(fn)},"body":[$body],"attrs":[$memberAttrs]${overridesJson(fn)}}"""
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
	val methods = (funMethods + propMethods).distinct().joinToString(",")
	// 2B layer 1: a Kotlin interface property -> a REAL CLR property (PropertyBuilder over its get_/set_ interface
	// methods), so a consumer (facadegen restoring the ref assembly) sees `size` as a PROPERTY, not a bare get_size
	// method. The accessor methods are already emitted (propMethods) named get_<n>/set_<n>; wire the property over them.
	val ifaceProps = iface.declarations.filterIsInstance<IrProperty>().filter { it.getter != null }.joinToString(",") { p ->
		val n = p.name.asString()
		val setName = if (p.setter != null) str("set_$n") else "null"
		"""{"name":${str(n)},"type":${birType(p.getter!!.returnType).toJson()},"get":${str("get_$n")},"set":$setName}"""
	}
	// A nested interface (`TimeSource.WithComparableMarks`) -> a real CLR nested type of its enclosing class/interface.
	val nestedIn = emittedNestedParent(iface)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null }
		?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
	val ifaces = iface.superTypes
		.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
		.mapNotNull { st ->
			val bt = birType(st)
			val stClass = st.classifierOrNull?.owner as? IrClass
			when {
				bt is TypeNode.Fn -> null
				stClass?.let { clrName(it) } != null -> bt.toJson()
				else -> stClass?.let { ownerSpec(it, st).toJson() }
			}
		}
		.joinToString(",")
	// Round-trip class-nature facts (Kotlin, not CLR) as structured `mods` (spec §2.1): `fun interface` (SAM) and
	// `sealed` — carried so a re-consuming Kotlin module can restore them (ilemit stamps [KotlinFunInterface]/
	// [KotlinSealed]; a plain CLR interface loses both).
	val funSealed = classModsJson(fnIface = iface.isFun, sealed = iface.modality == Modality.SEALED)
	return """{"name":${str(typeName(iface))},"kind":"interface"$nestedIn$funSealed${typeParamsJson(iface.typeParameters)},"base":null,"interfaces":[$ifaces],"fields":[],"ctors":[],"methods":[$methods],"properties":[$ifaceProps],"attrs":[${attrsJson(iface.annotations)}]}"""
}

/** A Kotlin `enum class` -> a real .NET enum (ilemit DefineEnum + literals). */
internal fun BirEmitter.enumDef(e: IrClass): String {
	val entries = e.declarations.filterIsInstance<IrEnumEntry>()
		.mapIndexed { i, ent -> """{"name":${str(ent.name.asString())},"ordinal":$i}""" }
	val nestedIn = emittedNestedParent(e)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null }
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
		.filter { !it.isCompanion && clrName(it) == null && it.name.asString() != "<no name provided>" }
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
		.filter { !it.isCompanion && clrName(it) == null && it.name.asString() != "<no name provided>" }
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
		.filter { clrName(it) == null && it.name.asString() != "<no name provided>" }
		.forEach { if (it.kind == ClassKind.ENUM_CLASS) out.add(it); out.addAll(nestedEnums(it)) }
	return out
}

/** Nested interfaces (recursively) inside a class OR interface (`TimeSource.WithComparableMarks`); emitted as real
 *  nested types so a supertype reference to the bare name resolves. */
internal fun BirEmitter.nestedInterfaces(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { clrName(it) == null && it.name.asString() != "<no name provided>" }
		.forEach { if (it.kind == ClassKind.INTERFACE) out.add(it); out.addAll(nestedInterfaces(it)) }
	return out
}

/** `inner class`es nested (recursively) inside a class -> flattened to top-level synthetic types. */
internal fun BirEmitter.innerClasses(c: IrClass): List<IrClass> {
	val out = ArrayList<IrClass>()
	c.declarations.filterIsInstance<IrClass>()
		.filter { it.kind == ClassKind.CLASS && !it.isCompanion && clrName(it) == null && it.name.asString() != "<no name provided>" }
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
 *  A .NET event is subscribed via `+=`/`-=` and is NEVER a first-class value or a real inherited property, so
 *  such a property must never be emitted as a member. This matters for a FAKE-OVERRIDE: when a Kotlin class
 *  subclasses a .NET type whose interface carries an event (`class MyApp : Avalonia.Application`, whose bases
 *  implement an event-bearing interface), fir2ir synthesizes a fake-override getter returning `ClrEvent<T>`;
 *  declaring it would emit an accessor/property over the un-emittable `kotlin.clr.ClrEvent` type — skip it. */
internal fun BirEmitter.isClrEventProperty(p: IrProperty): Boolean =
	p.getter?.returnType?.classFqName?.asString() == "kotlin.clr.ClrEvent"

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
	val savedRefCells = refCellVars
	refCellVars = refCellVars + computeRefCells(acc)
	val body = (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	refCellVars = savedRefCells
	if (extRecv != null) selfSubst.remove(extRecv)
	val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
	val ps = (listOfNotNull(selfParam) + paramsJsonList(acc.parameters)).joinToString(",")
	val name = (if (isGetter) "get_" else "set_") + propName
	val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
	return """{"name":${str(name)},"static":true,"override":false,"virtual":false,"abstract":false,"objectOverride":false,"vis":"public"${typeParamsJson(acc.typeParameters)},"params":[$ps],"ret":${str(ret)},"body":[$body]}"""
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
	val body = (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
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
	return """{"name":${str(mname)},"static":false,"override":$isOverrideClass,"virtual":$virtual,"abstract":$isAbstract,"objectOverride":false,"vis":${str(vis)},"params":[$ps],"ret":${str(ret)},"body":[$body]$accAttrs${overridesJson(acc)}}"""
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

/** The `attrs` JSON for a declaration: each annotation -> a .NET custom attribute application. A Kotlin-authored
 *  annotation is named by its plain Kotlin FQN (#46) — bir2cir derives its `: System.Attribute` base from the
 *  `"annotation":true` flag on the class def; an imported .NET attribute uses its real type via a `clr:` marker so
 *  ilemit binds the existing .NET constructor (#54).
 *
 *  kotc does NOT filter/select annotations: from kotc's view an annotation is just METADATA, so EVERY annotation is
 *  passed through to the BIR verbatim (incl. @ClrTypeAlias, @ClrIntrinsic, and every other `kotlin.*` annotation).
 *  The ref.dll consumer (bir2cir) is the CLR layer that decides what to do with each attribute. (The old keep-list —
 *  drop `kotlin.*` except @ClrIntrinsic/@ClrIntrinsicAsDynamic — was a kotc-side SELECT and is removed: a
 *  metadata-selection policy must NOT live in kotc.) If emitting some Kotlin-internal annotation type breaks
 *  downstream (its `: System.Attribute` type or an arg type being unresolvable at ilemit), that is a bir2cir/ilemit
 *  concern, NOT a reason to re-introduce a kotc filter. */
internal fun BirEmitter.attrsJson(anns: List<IrConstructorCall>): String {
	// kotc emits ONE BIR for every build: the roundtrip metadata ([Kotlin*]/[Clr]) rides EVERY
	// build's BIR verbatim — the rt-build metadata strip is downstream (ilemit skips ALL attrs under
	// `--build-stdlib=runtime`), so the rt.dll carries none while the ref/app BIR are byte-identical here.
	return anns.mapNotNull { ann ->
		val ac = ann.symbol.owner.parent as? IrClass ?: return@mapNotNull null
		if (ac.kind != ClassKind.ANNOTATION_CLASS) return@mapNotNull null
		val clr = clrName(ac)
		val attrType = if (clr != null) "clr:$clr" else typeName(ac)
		val args = regularArgs(ann)
		"""{"attr":${str(attrType)},"argTypes":[${args.joinToString(",") { birType(it.type).toJson() }}],"args":[${args.joinToString(",") { expr(it) }}]}"""
	}.joinToString(",")
}

internal fun BirEmitter.typeDef(klass: IrClass, captures: List<Pair<IrValueDeclaration, String>> = emptyList(), isObject: Boolean = false, liftedAnon: Boolean = false, generated: Boolean = false): String {
	val baseType = klass.superTypes
		.firstOrNull { val k = it.classifierOrNull?.owner as? IrClass; k != null && k.kind == ClassKind.CLASS && k.fqNameWhenAvailable?.asString() != "kotlin.Any" }
	val base = baseType?.classifierOrNull?.owner as? IrClass
	// A lifted anonymous-object class that CAPTURES enclosing generic type parameters (reified CLR generics —
	// `object : Box<T>`, or an inlined `object` whose supertype/captures resolve to the enclosing `T`) must be GENERIC
	// over them itself: on the CLR a `tv` referenced by its members is unresolved unless the flattened class DECLARES
	// the param and the construction site instantiates it with the enclosing arg (mirrors newClosure/newSam). This runs
	// ONLY for the lifted object-literal path (`liftedAnon`) — a normal named declaration owns all of its params — and
	// derives the captured set STRUCTURALLY from the class's real type positions (supertypes, own type-param bounds,
	// captured-var field types, ctor/member parameter + return + body-operand types). It deliberately does NOT scan a
	// member's CALL nodes: a `tv` inside a call's `sig` metadata is the CALLEE's own param (e.g. `clrCollAddAll<T>`),
	// NOT an enclosing capture — that over-captured, giving a normal `ArrayList<E>` a spurious `T` (arity-2, rt break).
	//
	// CRITICAL (the flip): the captured param `T` is declared on the ENCLOSING function/type, so birType renders every
	// member use of it as a scope="method"/"type" `tv` of that ENCLOSING owner — which is unresolvable once the anon is
	// flattened to a standalone generic class. So the scan+install runs BEFORE the members are rendered, and installs a
	// typeArgSubst remapping each captured param onto THIS class's own generic space (scope="type", the flattened index
	// AFTER the anon's own params). Rendering members then honors the remap → resolvable `{tv,type,i}`; restored at end.
	val ownTps = innerEnclosingTypeParams(klass) + klass.typeParameters
	val ownNames = ownTps.map { it.name.asString() }.toHashSet()
	val capturedTpParams = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
	if (liftedAnon) {
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
	val instFields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { p ->
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
	val fields = (instFields + statFields + capFields + instanceField).joinToString(",")
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
	val companionAccessors = companion?.declarations?.filterIsInstance<IrProperty>()
		?.filter { it.backingField == null }
		?.flatMap { p ->
			listOfNotNull(
				p.getter?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
				p.setter?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
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
	// has accessors with NO body — emitting them produced an empty-bodied get_name (ilverify ReturnMissing,
	// il-langf); CLR class inheritance provides the slot. An ABSTRACT fake-override resolved only to an INTERFACE
	// member (AbstractMutableList.size over MutableList.size) is KEPT: the CLR requires the (abstract) class to
	// re-declare the unimplemented interface slot.
	fun classInherited(a: IrSimpleFunction?) = (a?.resolveFakeOverride()?.parent as? IrClass)?.kind == ClassKind.CLASS
	fun dropFake(p: IrProperty) = p.isFakeOverride && classInherited(p.getter)
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
	val methods = (instMethods + statMethods + companionAccessors + userAccessors).joinToString(",")
	// A .NET base class (`: System.Exception(...)`, incl. a generic `: Collection<Int>()`) -> a `clr:`/`clrg:`
	// type spec (via birType) that ilemit resolves by reflection; a Kotlin-user base stays a bare type name.
	val baseJson = base?.let {
		// A .NET-injected base carries its full constructed identity (birType). A Kotlin-user/stdlib base emits its
		// IDENTITY: an inner-class base carries the enclosing args (`tv`) so the nested generic base is INSTANTIATED;
		// a non-inner generic base stays the OPEN name — ilemit walks the base chain by bare name and instantiates the
		// open generic base with this type's params positionally at SetParent. (bir2cir substitutes stdlib bases.)
		if (clrName(it) != null) birType(baseType!!).toJson()
		else {
			val enclArgs = innerEnclosingTypeParams(it).map { tp -> tvOf(tp) }
			(if (enclArgs.isNotEmpty()) TypeNode.Fqn(typeName(it), enclArgs) else TypeNode.Fqn(typeName(it))).toJson()
		}
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
				stClass?.let { clrName(it) } != null -> bt.toJson()
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
	val nestedIn = emittedNestedParent(klass)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null && !anonNames.containsKey(klass) && it.typeParameters.isEmpty() }
		?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
	// Round-trip: a Kotlin `sealed` class lowers to a CLR abstract class (loses the sealed modality) — carry the fact
	// so a re-consuming Kotlin module restores `sealed` (ilemit stamps [KotlinSealed]).
	val sealedFlag = classModsJson(sealed = klass.modality == Modality.SEALED)
	// typeParams = the anon/class's own params PLUS the captured enclosing params (scanned + installed at the top).
	val ownTpsJson = typeParamsJson(ownTps).removePrefix(""","typeParams":[""").removeSuffix("]")
	val extraJson = capturedTpParams.joinToString(",") { str(it.name.asString()) }
	val tpEntries = listOf(ownTpsJson, extraJson).filter { it.isNotEmpty() }.joinToString(",")
	val tpJson = if (tpEntries.isEmpty()) "" else ""","typeParams":[$tpEntries]"""
	// #68: a compiler-generated synthetic (a lifted anon-object / local class) carries `generated:true` — a STRUCTURAL
	// fact (no `<>` CLR-unspeakability marker; that is ilemit's concern). ilemit reads it to stamp [CompilerGenerated].
	val generatedFlag = if (generated) ""","generated":true""" else ""
	val result = """{"name":${str(typeName(klass))},"kind":"class","abstract":$isAbstract,"vis":${str(vis)}$nestedIn$sealedFlag$tpJson$generatedFlag,"base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods],"properties":[$propsList],"attrs":[${attrsJson(klass.annotations)}]}"""
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
		if (targetFq != "kotlin.Any") d.arguments.filterNotNull().joinToString(",") { expr(it) } else null
	} else null
	val stmts = ArrayList<String>()
	stmts.addAll(capAssigns)   // store captures before instance initializers, which may read them
	body?.statements?.forEach { s ->
		when (s) {
			is IrDelegatingConstructorCall -> {}
			is IrInstanceInitializerCall -> klass.declarations.forEach { d ->
				when (d) {
					is IrProperty -> d.backingField?.let { bf -> bf.initializer?.let {
						// Use the backing-field name (a delegated property's field is `<name>$delegate`).
						stmts.add("""{"k":"setField","ownerType":${fqnJson(typeName(klass))},"recv":{"k":"this"},"name":${str(bf.name.asString())},"value":${expr((it as IrExpressionBody).expression)}}""")
					} }
					is IrAnonymousInitializer -> (d.body as? IrBlockBody)?.statements?.forEach { stmts.add(stmt(it)) }
					else -> {}
				}
			}
			else -> stmts.add(stmt(s))
		}
	}
	val baseJson = baseArgs?.let { "[$it]" } ?: "null"
	val thisJson = thisArgs?.let { "[$it]" } ?: "null"
	return """{"params":[$params],"baseArgs":$baseJson,"thisArgs":$thisJson,"vis":${str(visOf(ctor))},"body":[${stmts.joinToString(",")}]}"""
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
	// Promote captured-mutated `var`s to ref-cells; accumulate (a nested closure inherits the enclosing set).
	val savedRefCells = refCellVars
	refCellVars = refCellVars + computeRefCells(fn)
	// `tailrec` tail-call optimization (§2b): if this is a `tailrec` fn with an actual self-tail-call, install a
	// TailrecCtx so each tail call emits a back-jump (tailrecJump) instead of recursing, and prefix the body with
	// the entry label the jumps target. The frontend already validated the tail positions; we reuse its own
	// collectTailRecursionCalls. Restored after the body so a nested/sibling fn is unaffected.
	val savedTailrec = tailrecCtx
	val tailrecStart: Int? = if (fn.isTailrec) {
		val tc = collectTailRecursionCalls(fn) { false }.ir
		if (tc.isNotEmpty()) cfgFresh().also { tailrecCtx = BirEmitter.TailrecCtx(tc, it, fn) } else null
	} else null
	val bodyStmts = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
	tailrecCtx = savedTailrec
	val body = if (tailrecStart != null) """{"k":"label","id":$tailrecStart}${if (bodyStmts.isNotEmpty()) ",$bodyStmts" else ""}""" else bodyStmts
	refCellVars = savedRefCells
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
	return """{"name":${str(emitName)},"static":$static,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${isAnySlot},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}$mods${resultTypeJson(fn)},"params":[$ps],"ret":${birType(fn.returnType).toJson()},"body":[$body],"attrs":[${attrsJson(fn.annotations)}]${overridesJson(fn)}}"""
}

/** Structured declaration-modifier object (spec §2.1): a single `"mods":{name:true,…}` carrying ONLY the set flags
 *  (absent key = not set), replacing the order-dependent `$kmods$inlineFlag$suspendField` fragment concatenation.
 *  `inline` = the "inline body must travel" fact (isInlineWithLambda), the only inline shape ilemit splices. */
internal fun BirEmitter.funModsJson(fn: IrSimpleFunction, inline: Boolean = false): String {
	val flags = buildList {
		if (inline) add(""""inline":true""")
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
 *  (`fun`-interface, `sealed`, `annotation`, …) — only the set flags, absent = not set. */
internal fun BirEmitter.classModsJson(fnIface: Boolean = false, sealed: Boolean = false, annotation: Boolean = false): String {
	val flags = buildList {
		if (annotation) add(""""annotation":true""")
		if (fnIface) add(""""fun":true""")
		if (sealed) add(""""sealed":true""")
	}
	return if (flags.isEmpty()) "" else ""","mods":{${flags.joinToString(",")}}"""
}

/** An `inline fun` with at least one (inlinable) lambda parameter — the only inline shape whose body must travel
 *  for cross-module consumption (lambda-less inline funs degrade to ordinary calls; the JIT inlines those). */
internal fun BirEmitter.isInlineWithLambda(fn: IrSimpleFunction): Boolean =
	fn.isInline && fn.parameters.any { it.kind == IrParameterKind.Regular && !it.isNoinline && birType(it.type) is TypeNode.Fn }

// ===== Coroutine SUSPEND FACTS (kotc emits facts only; ALL coroutine lowering is bir2cir's) =====
// kotc does NO coroutine lowering. A `suspend fun`/lambda body emits PLAINLY: decls carry `"suspend":true`
// (+ `resultType`), suspend call sites carry `"suspendCall":true`, and a suspend lambda emits `newSuspendLambda`.
// bir2cir consumes those facts to build the `ContinuationImpl` state machine + the public `Task<T>` bridge; kotc
// bakes NO coroutine ABI. The helpers below (isAwaitIntrinsic / isSuspensionCall / containsSuspend) are the ONLY
// coroutine code left in kotc, and they exist purely to DRIVE the fact emission (skip the await intrinsic method,
// tag suspend calls).
internal fun BirEmitter.isAwaitIntrinsic(fn: IrSimpleFunction): Boolean =
	fn.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrAwait" }

/** A suspension point: any call to a suspend function (the `.await()` intrinsic or a direct suspend call). */
internal fun BirEmitter.isSuspensionCall(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
	e is IrCall && e.symbol.owner.isSuspend

internal fun BirEmitter.containsSuspend(e: org.jetbrains.kotlin.ir.IrElement): Boolean {
	var found = false
	e.acceptVoid(object : IrVisitorVoid() {
		override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
			if (found) return
			// A scope function (`with`/`run`/`let`/`apply`/`also`) is INLINE -> a suspension in its lambda IS the
			// enclosing coroutine's. Descend into the lambda body (the receiver/other children are visited normally).
			scopeCall(element)?.let { (_, _, lambda) -> lambda.function.body?.let { if (containsSuspend(it)) { found = true; return } } }
			if (found) return
			// A non-inline nested lambda / local fun is a SEPARATE coroutine — its suspensions aren't the enclosing one's.
			if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
			if (isSuspensionCall(element)) { found = true; return }
			element.acceptChildrenVoid(this)
		}
	})
	return found
}

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

/** True if `fn` is a top-level / extension function (static-emitted, dispatch-receiver-less, non-suspend) with ANY
 *  defaulted parameter. Then ALL its defaulted params carry `@KotlinDefault` — the UNIFORM per-parameter splice source
 *  bir2cir uses to fill an omitted arg POSITIONALLY (Tier-1 and Tier-2 alike). This MUST cover Tier-1 too: at a
 *  CROSS-MODULE call kotc sees the callee's default as an IrErrorExpression (the frontend jar drops the VALUE) and so
 *  cannot tell Tier-1 from Tier-2 — it emits a `defaultArg` placeholder for EVERY omitted default, which bir2cir can
 *  only fill if a `@KotlinDefault` exists for that slot. (Tier-1 params ALSO keep the native `[Optional]` +
 *  `[DefaultParameterValue]` metadata for a C#/VB/F# consumer — that path is unchanged; `@KotlinDefault` is the
 *  kcc-consumer splice source, ref.dll-only, stripped from the runtime build.) */
internal fun BirEmitter.carriesKotlinDefault(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
	!fn.isSuspend && fn.parameters.none { it.kind == IrParameterKind.DispatchReceiver } &&
		fn.parameters.any { it.kind == IrParameterKind.Regular && it.defaultValue != null }

/** A data-class `copy` synthetic — `copy` cannot be user-declared on a data class, so name + `isData` parent is exact. */
internal fun BirEmitter.isDataClassCopy(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
	fn.name.asString() == "copy" && (fn.parent as? IrClass)?.isData == true

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
				val bir = expr(def)   // BIR of the default expression (real IR here — the callee's own build)
				"""{"attr":"kotlin.clr.KotlinDefault","argTypes":[${fqnJson("kotlin.Int")},${fqnJson("kotlin.String")}],"args":[{"k":"const","type":${fqnJson("kotlin.Int")},"value":${regIdx + extOffset}},{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(bir)}}]}"""
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
