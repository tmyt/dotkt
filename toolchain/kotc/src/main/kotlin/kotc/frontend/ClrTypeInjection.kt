@file:OptIn(
	org.jetbrains.kotlin.fir.extensions.FirExtensionApiInternals::class,
	org.jetbrains.kotlin.fir.extensions.ExperimentalTopLevelDeclarationsGenerationApi::class,
	org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
)

package kotc.frontend

import java.io.File
import kotc.bir.TypeNode
import org.jetbrains.kotlin.GeneratedDeclarationKey
import org.jetbrains.kotlin.compiler.plugin.CompilerPluginRegistrar
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.extensions.FirDeclarationGenerationExtension
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrarAdapter
import org.jetbrains.kotlin.fir.extensions.MemberGenerationContext
import org.jetbrains.kotlin.fir.extensions.NestedClassGenerationContext
import org.jetbrains.kotlin.fir.plugin.createCompanionObject
import org.jetbrains.kotlin.fir.plugin.createConstructor
import org.jetbrains.kotlin.fir.plugin.createMemberFunction
import org.jetbrains.kotlin.fir.plugin.createMemberProperty
import org.jetbrains.kotlin.fir.plugin.createTopLevelProperty
import org.jetbrains.kotlin.fir.plugin.createTopLevelClass
import org.jetbrains.kotlin.fir.plugin.createTopLevelFunction
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.fir.symbols.impl.FirClassLikeSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirClassSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirConstructorSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirPropertySymbol
import org.jetbrains.kotlin.fir.declarations.FirFunction
import org.jetbrains.kotlin.fir.expressions.FirExpression
import org.jetbrains.kotlin.fir.expressions.builder.buildLiteralExpression
import org.jetbrains.kotlin.types.ConstantValueKind
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.fir.types.ConeFlexibleType
import org.jetbrains.kotlin.fir.types.ConeRigidType
import org.jetbrains.kotlin.fir.types.constructType
import org.jetbrains.kotlin.fir.types.typeContext
import org.jetbrains.kotlin.fir.types.withNullability
import org.jetbrains.kotlin.fir.types.impl.ConeClassLikeTypeImpl
import org.jetbrains.kotlin.fir.symbols.impl.ConeClassLikeLookupTagImpl
import org.jetbrains.kotlin.name.CallableId
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.name.SpecialNames

/** Marks declarations our extension synthesizes (required by the FIR plugin builder helpers). */
object ClrGeneratedKey : GeneratedDeclarationKey()

// ----- metadata model (produced by `facadegen --meta`, read here) -----

// A restored default-arg value (`{valueType, value}`): `valueType` = the primitive kind (Int/Long/String/…) the
// consumer builds a FirLiteralExpression of; `value` = the literal as a string, or NULL for a `null` default.
private class ClrDefault(val valueType: String, val value: String?)
// `type` is a structured TypeNode (spec §1); `vararg` -> the param is a vararg whose ELEMENT type is `type`.
private class ClrParam(val name: String, val type: TypeNode, val vararg: Boolean = false, val default: ClrDefault? = null)
// A generic type parameter: its name, declaration-site `variance` (interfaces), and upper `bounds` (`<T : Comparable<T>>`).
private class ClrTypeParam(val name: String, val variance: String? = null, val bounds: List<TypeNode> = emptyList())
// `open`/`abstract` = .NET virtual/abstract (Kotlin OPEN/ABSTRACT modality); `protected` = .NET Family/FamORAssem.
// `infix`/`operator`/`suspend`/`inline`/`ext` = Kotlin modifiers restored from the `mods` object. For a `suspend` fun
// the returnType is already the unwrapped result T (facadegen unwrapped the emitted Task<T>).
private class ClrMethod(val name: String, val returnType: TypeNode, val open: Boolean, val abstract: Boolean, val protected: Boolean, val params: List<ClrParam>, val typeParams: List<ClrTypeParam> = emptyList(),
	val infix: Boolean = false, val operator: Boolean = false, val suspend: Boolean = false, val inline: Boolean = false, val ext: Boolean = false)
// A restored top-level Kotlin function: its package, the .NET file-facade class to call, and the function itself.
private class ClrTopLevel(val pkg: FqName, val fileClassDotNet: String, val fn: ClrMethod)
// N5: one file-class candidate for a top-level CallableId (`.NET file class` + the value-param arity range it covers).
// Several candidates under one CallableId = same-name same-package overloads across DIFFERENT source files; the backend
// disambiguates by the resolved callee's arity. See `clrInjectedTopLevelFileClass`.
internal class TopLevelSig(val fileClass: String, val minArity: Int, val maxArity: Int)

// Unsigned Kotlin types (scalar + specialized array) live in the `kotlin` package as LIBRARY types — they have no
// `bt.*` builtin, so a facadegen-injected reference to one resolves straight off the symbol provider by ClassId, not
// via the .NET-injected `classIdFor` (which knows nothing about them). #53.
internal val UNSIGNED_KOTLIN_TYPES = setOf(
	"UByte", "UShort", "UInt", "ULong", "UByteArray", "UShortArray", "UIntArray", "ULongArray",
)
// A top-level property: `receiver` (a TypeNode) present => an EXTENSION property (`val T.p`); null => a plain top-level prop.
private class ClrTopLevelProp(val pkg: FqName, val fileClassDotNet: String, val name: String, val type: TypeNode, val mutable: Boolean, val receiver: TypeNode?)
private class ClrProperty(val name: String, val type: TypeNode, val mutable: Boolean, val open: Boolean, val abstract: Boolean, val protected: Boolean)
// A MEMBER extension property (`class C { val T.p }`): restored as a member property of C with an extension receiver.
private class ClrMemberExtProp(val name: String, val type: TypeNode, val mutable: Boolean, val receiver: TypeNode, val protected: Boolean)
private class ClrEvent(val name: String, val handlerReturn: TypeNode, val handlerParams: List<ClrParam>)
// A `this[i]` indexer -> Kotlin `operator fun get/set` (`set` only when mutable).
private class ClrIndexer(val indexType: TypeNode, val valueType: TypeNode, val mutable: Boolean)
private class ClrType(
	val kotlinName: String,
	val dotNetName: String,
	val isObject: Boolean,
	val isInterface: Boolean,          // .NET interface => Kotlin can implement it
	val isAnnotation: Boolean,         // System.Attribute-derived => Kotlin annotation class (apply on decls)
	val open: Boolean,                 // .NET non-sealed => Kotlin can extend it
	val typeParams: List<ClrTypeParam>,// generic type parameters (name + variance + bounds)
	val superTypes: List<TypeNode>,    // injectable base class + interfaces — wired by ClrSupertypeInjector
	val methods: List<ClrMethod>,
	val ctors: List<List<ClrParam>>,
	val properties: List<ClrProperty>,
	val events: List<ClrEvent>,
	val indexer: ClrIndexer?,
	val baseNoArgCtor: Boolean,        // false: base lacks a no-arg ctor -> don't synthesize `: super()`
	val staticMethods: List<ClrMethod>,// public static methods of a NORMAL class -> companion-object members (App.Start)
	val staticProps: List<ClrProperty>,// public static props/fields of a NORMAL class -> companion-object members
	val staticEvents: List<ClrEvent> = emptyList(),  // (N6) public STATIC events of a NORMAL class -> companion `ClrEvent<T>` props
	val memberExtProps: List<ClrMemberExtProp> = emptyList(),  // `class C { val T.p }` member extension properties
	val clrBinding: String? = null,    // ref/runtime split: the BCL type this Kotlin type binds to (`List` -> IReadOnlyList)
	val isFunInterface: Boolean = false,   // round-trip: was a Kotlin `fun interface` (SAM) — restore `status.isFun`
	val isSealed: Boolean = false,         // round-trip: was a Kotlin `sealed` class/interface — restore Modality.SEALED
	val iteratorElem: TypeNode? = null,    // IEnumerable<T> element -> a frontend-only `operator fun iterator(): Iterator<T>`
)
private class ClrModule(val types: List<ClrType>, val topLevel: List<ClrTopLevel> = emptyList(), val topLevelProps: List<ClrTopLevelProp> = emptyList())

// (N6) A normal .NET class gets a synthesized companion object iff it has public statics — methods, props/fields, OR
// events (`TaskScheduler.UnobservedTaskException`). All companion-gating sites funnel through this so a class whose
// ONLY statics are events still materializes its companion (and its `ClrEvent<T>` static-event properties).
private val ClrType.hasStatics: Boolean get() = staticMethods.isNotEmpty() || staticProps.isNotEmpty() || staticEvents.isNotEmpty()

/**
 * A2 keystone (interop-no-registry, stage 1): the backend reads an injected .NET type's name off its IR `ClassId`
 * through this accessor — facadegen's metadata keyed by the resolved ClassId, with the generic-arity backtick stripped
 * (`System.Threading.Tasks.Task\`1` -> `Task`; ilemit re-appends `\`N` from the arg count).
 * Null for a non-injected class (user Kotlin type / stdlib). File-top-level so
 * it can reach the file-private [ClrMetadataHolder] while exposing only public types across the module boundary.
 */
internal fun clrInjectedDotNetName(classId: ClassId): String? = ClrMetadataHolder.dotNetNameByClassId[classId]

// A2 step 5 (interop member-slot -> bir2cir): the backend no longer reads an injected .NET MEMBER's slot name here. A
// Kotlin member overriding a facadegen-injected .NET interface/base binds its slot in bir2cir's DeclarationRename, which
// reflects the owner .NET Type off the refs (a method -> its name; a property accessor -> get_/set_ + the .NET property
// name; facadegen injects the Kotlin identity EQUAL to the .NET name). So there is no member-name accessor / metadata map.

/**
 * A2 keystone (interop-no-registry, stage 3): the backend reads a restored DotKt TOP-LEVEL function's .NET file-facade
 * class (`LibKt`) off its resolved IR `CallableId` (`package` + name) through this accessor — facadegen's metadata keyed
 * by that same structural identity. FIR/Fir2Ir already resolved the call to a UNIQUE callee, so the resolved
 * callee's CallableId keys the fact directly (no candidate list to disambiguate). Null for a
 * non-restored (local / stdlib-from-jar) top-level fun. Suspend-ness is NOT carried here: the backend reads it off the
 * resolved callee (`isSuspend`) via `suspendCallTag`, so re-carrying it would re-introduce the resolved-fact-by-name
 * anti-pattern this stage removes.
 */
//
// N5 fix (overload-aware): the CallableId is `(package, name)` ONLY, so two same-name same-package top-level overloads
// living in DIFFERENT source files (`foo()` in `UtilsKt`, `foo(Int)` in `HelpersKt`) collided on the key alone — the
// flat `Map<CallableId,String>` collapsed to LAST-PUT-WINS and mis-routed one of them to the wrong file class (a hard
// ilemit "method not found"). This was a regression the A2 registry-removal introduced (the deleted receiver-
// discriminator arbitrary-picked first; the flat map arbitrary-picked last). Fixed by carrying ALL file-class
// candidates for a CallableId and disambiguating by the RESOLVED callee's value-param `arity` (the metadata `tlfun`
// param count). A single (non-colliding) candidate is returned directly, so A2's byte-identical routing is preserved.
internal fun clrInjectedTopLevelFileClass(callableId: CallableId, arity: Int): String? {
	val sigs = ClrMetadataHolder.topLevelSigByCallableId[callableId] ?: return null
	// A2 byte-identical: a UNIQUE restored overload for this (package,name) -> its file class directly (the common
	// case; a single `tlfun` spans an arity RANGE across its default-arg variants, but there is one file class either way).
	if (sigs.size == 1) return sigs[0].fileClass
	// N5: multiple file classes share this CallableId -> pick by the resolved callee's value-param arity. FIR already
	// resolved the call to a UNIQUE overload, so its arity lands in exactly one candidate's range -> 1:1 routing.
	return (sigs.firstOrNull { arity in it.minArity..it.maxArity } ?: sigs.first()).fileClass
}

/**
 * A2 keystone (interop-no-registry, stage 3): the backend reads a restored DotKt TOP-LEVEL EXTENSION PROPERTY's .NET
 * file-facade class (its `get_`/`set_<name>` statics live there) off its resolved IR `CallableId` (`package` + name) —
 * facadegen's metadata keyed structurally.
 * Null for a non-restored top-level property.
 */
internal fun clrInjectedTopLevelPropFileClass(callableId: CallableId): String? = ClrMetadataHolder.fileClassByTopLevelPropCallableId[callableId]

/**
 * Loads the .NET type metadata to inject, once per process. The path comes from `CLR_TYPES_METADATA`
 * (set by the build / MSBuild / verify harness). Absent or empty => inject nothing, so compilations
 * that don't opt in are completely unaffected. The backend reads each injected type's .NET name off its
 * IR `ClassId` via [clrInjectedDotNetName]. A member's .NET slot name is NOT resolved here — bir2cir's DeclarationRename
 * reflects it off the refs (A2 step 5). A .NET EVENT no longer needs any
 * side-channel at all: it is surfaced as a `ClrEvent<T>` property and subscribed via `+=`/`-=`, which bir2cir binds
 * to the add/remove accessor. All interop facts are keyed off the resolved IR identity — no name-keyed side-channel.
 */
private object ClrMetadataHolder {
	val module: ClrModule? by lazy { System.getenv("CLR_TYPES_METADATA")?.let { load(File(it)) } }

	// The injection metadata is now a structured JSON document (spec §5b): `{ "types": [...], "files": [...] }`,
	// reusing the BIR TypeNode / mods / decl vocabulary. The walk below reconstructs the ClrType/ClrTopLevel model;
	// there is no line grammar, no token-splitting, no type-string prefix parse (all types are TypeNode nodes).
	@Suppress("UNCHECKED_CAST")
	private fun load(file: File): ClrModule? {
		if (!file.isFile) return null
		val root = TypeNode.parseJsonValue(file.readText()) as? Map<String, Any?> ?: return null
		val types = (root["types"] as? List<Any?>).orEmpty().map { readType(it as Map<String, Any?>) }
		val topLevel = ArrayList<ClrTopLevel>(); val topLevelProps = ArrayList<ClrTopLevelProp>()
		for (f in (root["files"] as? List<Any?>).orEmpty()) {
			val fo = f as Map<String, Any?>
			val pkg = (fo["pkg"] as? String).let { if (it.isNullOrEmpty()) FqName.ROOT else FqName(it) }
			val fileClass = fo["fileClass"] as? String ?: ""
			for (fn in (fo["funs"] as? List<Any?>).orEmpty())
				topLevel.add(ClrTopLevel(pkg, fileClass, readFun(fn as Map<String, Any?>)))
			for (pp in (fo["props"] as? List<Any?>).orEmpty()) {
				val p = pp as Map<String, Any?>
				topLevelProps.add(ClrTopLevelProp(pkg, fileClass, p["name"] as String, typeOf(p["type"]),
					p["rw"] == true, (p["recv"])?.let { typeOf(it) }))
			}
		}
		return ClrModule(types, topLevel, topLevelProps)
	}

	private fun typeOf(v: Any?): TypeNode = TypeNode.fromJsonValue(v)
	@Suppress("UNCHECKED_CAST")
	private fun modsOf(v: Any?): Set<String> = (v as? Map<String, Any?>)?.filterValues { it == true }?.keys ?: emptySet()

	@Suppress("UNCHECKED_CAST")
	private fun readParam(o: Map<String, Any?>): ClrParam {
		val default = (o["default"] as? Map<String, Any?>)?.let { ClrDefault(it["valueType"] as? String ?: "", it["value"] as? String) }
		return ClrParam(o["name"] as String, typeOf(o["type"]), "vararg" in modsOf(o["mods"]), default)
	}
	@Suppress("UNCHECKED_CAST")
	private fun readParams(v: Any?): List<ClrParam> = (v as? List<Any?>).orEmpty().map { readParam(it as Map<String, Any?>) }
	@Suppress("UNCHECKED_CAST")
	private fun readTypeParam(o: Map<String, Any?>): ClrTypeParam =
		ClrTypeParam(o["name"] as String, o["variance"] as? String, (o["bounds"] as? List<Any?>).orEmpty().map { typeOf(it) })
	@Suppress("UNCHECKED_CAST")
	private fun readTypeParams(v: Any?): List<ClrTypeParam> = (v as? List<Any?>).orEmpty().map { readTypeParam(it as Map<String, Any?>) }

	@Suppress("UNCHECKED_CAST")
	private fun readFun(o: Map<String, Any?>): ClrMethod {
		val mods = modsOf(o["mods"]); val vis = o["vis"] as? String ?: "public"
		return ClrMethod(o["name"] as String, typeOf(o["ret"]), "open" in mods, "abstract" in mods, vis == "protected",
			readParams(o["params"]), readTypeParams(o["typeParams"]),
			"infix" in mods, "operator" in mods, "suspend" in mods, "inline" in mods, "ext" in mods)
	}
	@Suppress("UNCHECKED_CAST")
	private fun readProp(o: Map<String, Any?>): ClrProperty {
		val mods = modsOf(o["mods"]); val vis = o["vis"] as? String ?: "public"
		return ClrProperty(o["name"] as String, typeOf(o["type"]), o["rw"] == true, "open" in mods, "abstract" in mods, vis == "protected")
	}
	@Suppress("UNCHECKED_CAST")
	private fun readEvent(o: Map<String, Any?>): ClrEvent =
		ClrEvent(o["name"] as String, typeOf(o["handlerRet"]), readParams(o["handlerParams"]))

	@Suppress("UNCHECKED_CAST")
	private fun readType(o: Map<String, Any?>): ClrType {
		val kind = o["kind"] as? String
		fun props(k: String) = (o[k] as? List<Any?>).orEmpty().map { readProp(it as Map<String, Any?>) }
		fun funs(k: String) = (o[k] as? List<Any?>).orEmpty().map { readFun(it as Map<String, Any?>) }
		fun events(k: String) = (o[k] as? List<Any?>).orEmpty().map { readEvent(it as Map<String, Any?>) }
		val memberExtProps = (o["memberExtProps"] as? List<Any?>).orEmpty().map {
			val p = it as Map<String, Any?>
			ClrMemberExtProp(p["name"] as String, typeOf(p["type"]), p["rw"] == true, typeOf(p["recv"]), (p["vis"] as? String) == "protected")
		}
		val ctors = (o["ctors"] as? List<Any?>).orEmpty().map { readParams((it as Map<String, Any?>)["params"]) }
		val indexer = (o["indexer"] as? Map<String, Any?>)?.let { ClrIndexer(typeOf(it["indexType"]), typeOf(it["valueType"]), it["rw"] == true) }
		return ClrType(
			o["name"] as String, o["dotNet"] as String,
			isObject = kind == "object", isInterface = kind == "interface", isAnnotation = kind == "annotation",
			open = o["open"] == true, typeParams = readTypeParams(o["typeParams"]),
			superTypes = (o["supers"] as? List<Any?>).orEmpty().map { typeOf(it) },
			methods = funs("funs"), ctors = ctors, properties = props("props"), events = events("events"),
			indexer = indexer, baseNoArgCtor = o["baseNoArgCtor"] != false,
			staticMethods = funs("staticFuns"), staticProps = props("staticProps"), staticEvents = events("staticEvents"),
			memberExtProps = memberExtProps, clrBinding = o["clrBinding"] as? String,
			isFunInterface = o["funInterface"] == true, isSealed = o["sealed"] == true,
			iteratorElem = o["iteratorElem"]?.let { typeOf(it) },
		)
	}

	// Shared lookups (also used by ClrSupertypeInjector). Each .NET type resolves at its REAL namespace, so
	// `import System.Text.StringBuilder` works through Kotlin's normal package machinery (the .NET namespace IS the
	// Kotlin package). The .NET-name token is the TRUE CLR name — a generic definition carries its backtick arity
	// (`System.Threading.Tasks.Task`1`) and a nested type its '+' — both live in the SIMPLE-name part, so the
	// namespace is everything before the last '.' of the '+'-stripped form.
	fun namespaceOf(dotNet: String): String = dotNet.substringBefore('+').substringBeforeLast('.', "")
	val byClassId: Map<ClassId, ClrType> by lazy {
		// ref/runtime split: a @Clr stdlib type (clrBinding != null, e.g. List/Collection) is a builtin the jar/
		// frontend already provides. Only its BCL binding is registered (above) for the backend's clrName; do NOT
		// re-create it as a FIR type here, or it shadows the jar's builtin and loses operator/infix modifiers.
		// Broader: NO `kotlin.*` type is injected — the jar/frontend owns ALL Kotlin shapes (Iterator/Iterable/...);
		// ref.meta is consulted ONLY for the @Clr bindings (registered above). The injection creates System.* types only.
		module?.types?.filter { it.clrBinding == null && !it.dotNetName.startsWith("kotlin.") }?.associateBy { ClassId(FqName(namespaceOf(it.dotNetName)), Name.identifier(it.kotlinName)) }.orEmpty()
	}
	val classIdByName: Map<String, ClassId> by lazy { byClassId.entries.associate { (id, t) -> t.kotlinName to id } }
	// A2 keystone (interop-no-registry stage 1): the backend's clrName reads the injected type's .NET name straight off
	// its IR `ClassId` (a structural, resolved identity — no name-keyed injector-populated side-channel). This map is
	// facadegen's own metadata keyed by that same ClassId; the value is the type's TRUE .NET name with its generic-arity
	// backtick stripped (`System.Threading.Tasks.Task\`1` -> `Task`), matching the backend contract (BirEmitter emits the
	// arity-LESS open name in `clrg:<open>[args]`; ilemit re-appends `\`N` from the constructed arg count). This is a pure
	// projection of `byClassId`, which already excludes @Clr-bound stdlib types (clrBinding != null) and `kotlin.*` — a
	// facadegen-injected stdlib type never happens (kotlin.* comes from the JAR). NOTE: the .NET name of an arity-QUALIFIED Kotlin name
	// (`Task\`1` -> Kotlin `Task1`) genuinely diverges from the ClassId simple name, so it must be carried (facadegen's
	// fact), not re-derived from the ClassId string — hence this metadata read rather than `classId.asString()` alone.
	val dotNetNameByClassId: Map<ClassId, String> by lazy { byClassId.mapValues { it.value.dotNetName.substringBefore('`') } }
		// (A2 step 5: the injected-MEMBER .NET-slot-name map is GONE -- a member's slot is resolved in bir2cir's
		// DeclarationRename by reflecting the owner .NET Type off the refs, not from a kotc-side metadata map.)
	// A2 keystone (interop-no-registry stage 3): a restored DotKt top-level function's .NET file-facade class keyed by its
	// resolved IR `CallableId` (`package`/name — exactly the CallableId the injector builds in `topLevelByCallable`, so it
	// matches the resolved Fir2Ir callee). Because FIR already resolved every call to a UNIQUE callee, there is a single
	// fileClass per CallableId (nothing left to disambiguate). The `Clr`-file-class strip
	// (`<Common>ClrKt` -> `<Common>Kt`, an rt-vs-jar fact) is applied here (mirrors BirEmitter.fileClassName). Suspend is
	// NOT carried: the backend derives it from the resolved callee (`isSuspend`), so it stays a resolved fact, not a name map.
	// N5: a restored top-level fun's file-class candidate = (fileClass, the value-param arity RANGE it covers). A single
	// `tlfun` with default args injects several arities (`(vps-trailingOpt)..vps`), so a candidate spans a range; the
	// accessor picks the candidate whose range contains the resolved callee's arity. `ext` funs carry a leading `__self`
	// receiver param that is NOT a value param at the call site — drop it so the arity matches the backend's regularParams.
	val topLevelSigByCallableId: Map<CallableId, List<TopLevelSig>> by lazy {
		buildMap<CallableId, MutableList<TopLevelSig>> {
			for (tl in module?.topLevel.orEmpty()) {
				val vps = if (tl.fn.ext && tl.fn.params.isNotEmpty()) tl.fn.params.drop(1) else tl.fn.params
				val trailingOpt = vps.reversed().takeWhile { it.default != null }.count()
				getOrPut(CallableId(tl.pkg, Name.identifier(tl.fn.name))) { ArrayList() }
					.add(TopLevelSig(stripClrFileClass(tl.fileClassDotNet), vps.size - trailingOpt, vps.size))
			}
		}
	}
	// A2 keystone (interop-no-registry stage 3): a restored DotKt top-level EXTENSION PROPERTY's .NET file-facade class
	// (holding its `get_`/`set_<name>` statics) keyed by its resolved IR `CallableId`.
	val fileClassByTopLevelPropCallableId: Map<CallableId, String> by lazy {
		buildMap {
			for (tp in module?.topLevelProps.orEmpty())
				put(CallableId(tp.pkg, Name.identifier(tp.name)), stripClrFileClass(tp.fileClassDotNet))
		}
	}
	// Platform-actual files `<Common>Clr.kt` emit their actuals into the COMMON file class `<Common>Kt` -- ilemit/the rt
	// strip the `Clr` suffix (BirEmitter.fileClassName). The metadata's fileClass comes from the K2 frontend jar, which
	// does NOT strip, so a non-inline top-level call would reference `<Common>ClrKt` -- never emitted by the rt, giving
	// `cannot resolve .NET type ...ClrKt`. Strip here to match the rt. Mirrors fileClassName's `stem.endsWith("Clr")`.
	private fun stripClrFileClass(fc: String): String {
		val dot = fc.lastIndexOf('.'); val simple = if (dot >= 0) fc.substring(dot + 1) else fc
		return if (simple.endsWith("ClrKt")) (if (dot >= 0) fc.substring(0, dot + 1) else "") + simple.removeSuffix("ClrKt") + "Kt" else fc
	}
	// Generic and non-generic types share a simple name across NAMESPACES (`IEnumerable<T>` in .Generic vs the legacy
	// `IEnumerable`) — resolve by (name, arity) so `generic:IEnumerable[Item]` picks the generic one and a bare
	// `IEnumerable` picks the non-generic one. SAME-namespace families (Task/Task`1) no longer collide at all:
	// facadegen arity-qualifies the generic's KOTLIN name (Task`1 -> Task1, kotlin.Function1 precedent), so every
	// (kotlinName, arity) — and every ClassId in byClassId — is unique, and BOTH family members coexist in FIR.
	private val byNameArity: Map<Pair<String, Int>, ClassId> by lazy {
		byClassId.entries.associate { (id, t) -> (t.kotlinName to t.typeParams.size) to id }
	}
	// STRICT on arity, NO fallback. Resolving a reference to an ABSENT arity by falling back to a present one
	// builds a generic type with the wrong number of arguments and crashes the fir2ir fake-override builder
	// ("typeParameters size != typeArguments size"). Returning null instead just skips that reference (-> Any?/no edge).
	fun classIdFor(name: String, arity: Int): ClassId? = byNameArity[name to arity]
}

/**
 * M-S S5 — façade-free .NET type resolution, **metadata-driven**.
 *
 * Synthesizes the .NET types listed in the metadata file straight into FIR, so a user can
 * `import clrgen.Math; Math.Abs(-9)` / `Console.WriteLine(...)` with NO hand-written or generated
 * `@Clr` façade `.kt`. The metadata is produced by `facadegen --meta` reflecting over real .NET
 * assemblies, feeding the compiler in-memory.
 *
 * Synthesized FIR carries no annotations; the backend recovers each type's .NET name from its IR
 * `ClassId` (via [clrInjectedDotNetName]). Supported now: `object` (static) + `class` (constructors + instance
 * methods); members with primitive/String/Unit/self/other-injected-type signatures. Properties and
 * generics are the next slice (see docs/research-roadmap.md §S5).
 */
class ClrTypeInjector(session: FirSession) : FirDeclarationGenerationExtension(session) {
	private val module = ClrMetadataHolder.module
	// C-2: each .NET type resolves at its REAL namespace (shared lookups live on ClrMetadataHolder so the supertype
	// extension reuses them) — `import System.Text.StringBuilder` works through Kotlin's normal package machinery.
	private val byClassId: Map<ClassId, ClrType> = ClrMetadataHolder.byClassId
	private val packages: Set<FqName> = byClassId.keys.map { it.packageFqName }.toSet()
	// DotKt round-trip: top-level functions restored from [KotlinFile] facades, keyed by their CallableId (a package
	// may hold several; overloads share a CallableId). Their packages augment hasPackage/getTopLevelCallableIds.
	private val topLevelByCallable: Map<CallableId, List<ClrTopLevel>> =
		(module?.topLevel ?: emptyList()).groupBy { CallableId(it.pkg, Name.identifier(it.fn.name)) }
	// DotKt round-trip: top-level extension properties (`val T.p`), keyed by CallableId.
	private val topLevelPropByCallable: Map<CallableId, ClrTopLevelProp> =
		(module?.topLevelProps ?: emptyList()).associateBy { CallableId(it.pkg, Name.identifier(it.name)) }
	private val topLevelPackages: Set<FqName> =
		((module?.topLevel ?: emptyList()).map { it.pkg } + (module?.topLevelProps ?: emptyList()).map { it.pkg }).toSet()
	private val classIdByName: Map<String, ClassId> = ClrMetadataHolder.classIdByName

	// `byref(x)`: an intrinsic marking a call arg as a .NET out/ref parameter. It returns `ClrRef<T>` (the surfaced type
	// of any .NET byref param), so the signature is self-documenting. The backend reads the marker and passes the
	// lvalue's address; a `ClrRef<T>` param maps to `byref:T`. byref/ClrRef live in the `kotlin.clr` namespace (the CLR-
	// intrinsic home, alongside @kotlin.clr.ClrIntrinsic) so they're IMPORTABLE from a named package — unlike a root-
	// package symbol, which Kotlin cannot import into a named package (that blocked the packaged stdlib from using them).
	private val byrefName = "byref"
	private val clrPkg = FqName("kotlin.clr")
	// `ClrRef<T>`: an intrinsic generic type for a managed reference (T&). It is the surfaced type of a .NET out/ref
	// parameter and of a ref-returning method; it is `by`-delegatable (getValue/setValue) so a ref return reads as
	// `var x by m()`. The argument path erases it (the byref(x) marker emits the lvalue's address).
	private val clrRefClassId = ClassId(clrPkg, Name.identifier("ClrRef"))
	// `stackBuffer(n) { buf -> … }` + `StackBuffer<T>`: a scoped stack allocation (CLR `localloc`). The block is
	// splice-inlined so the buffer lives in the caller's frame; `StackBuffer<T>` (size/get/set/asSpan) is erased.
	// Live in the `kotlin.clr` namespace (the CLR-intrinsic home, alongside `ClrRef`/`byref`) so they're IMPORTABLE
	// from a named package — unlike a root-package symbol, which Kotlin cannot import into a named package.
	private val stackBufferName = "stackBuffer"
	private val stackBufferClassId = ClassId(clrPkg, Name.identifier("StackBuffer"))
	// `Span<T>`: a `kotlin.clr` intrinsic that maps to the real `System.Span<T>` (birType -> clrg:System.Span)
	// — the surfaced form of a .NET Span parameter and the result of `StackBuffer.asSpan()`.
	private val spanClassId = ClassId(clrPkg, Name.identifier("Span"))
	// `ClrEvent<T>`: a compile-time-only fiction for the idiomatic `.NET event` subscription (`w.Changed += handler`).
	// A .NET event is NOT a first-class value (you can only add/remove/raise it), so `w.Changed` NEVER materializes a
	// ClrEvent<T> at runtime -- it is a handle whose only purpose is to make the Kotlin `+=`/`-=` operators resolve. The
	// event member is surfaced as a read-only property `Changed: ClrEvent<HandlerFn>` (T = the handler's Kotlin function
	// type); ClrEvent<T> carries `operator fun plusAssign(handler: T)` / `minusAssign(handler: T)` (no body -- never
	// executed). bir2cir's ClrEventOperatorBinding rewrites `w.Changed.plusAssign(h)` -> the .NET add-accessor node
	// (clrEventAdd) before emit, so this type is a pure frontend-resolution fiction -- NOT a shipped stdlib type. Lives
	// in `kotlin.clr` (the CLR-intrinsic home, alongside `ClrRef`/`Span`), and never reaches ilemit.
	private val clrEventClassId = ClassId(clrPkg, Name.identifier("ClrEvent"))
	// The intrinsics are CLR-context features -> available whenever .NET interop is active (metadata loaded).
	private val clrActive = module != null

	// Companions created EAGERLY in generateTopLevelClassLikeDeclaration (statics -> implicit `App.Start` support),
	// keyed by companion ClassId. Per-extension-instance (= per-session); generateNestedClassLikeDeclaration must
	// return the SAME instance — a second FirRegularClassSymbol for one ClassId breaks provider/fir2ir identity.
	private val eagerCompanions = java.util.concurrent.ConcurrentHashMap<ClassId, org.jetbrains.kotlin.fir.declarations.FirRegularClass>()

	// `byref`/`ClrRef` are PURE compiler intrinsics (no .NET metadata needed) -> the `kotlin.clr` package is ALWAYS
	// claimed so the packaged stdlib — built with CLR_TYPES_METADATA="" (module==null -> clrActive==false) — can
	// `import kotlin.clr.byref` / `ClrRef` to pass a field BY REFERENCE to a BCL `ref`/`out` method (the atomics'
	// Interlocked, int.TryParse, Math.DivRem). The metadata-backed packages (System.*, the round-trip facades) and the
	// metadata-context intrinsics (stackBuffer/Span) stay clrActive-gated — but they now ALSO live under `kotlin.clr`
	// (claimed above), so no root-package claim is needed (a root [KotlinFile] facade fun is covered by topLevelPackages).
	override fun hasPackage(packageFqName: FqName): Boolean =
		packageFqName == clrPkg || (clrActive && (packageFqName in packages || packageFqName in topLevelPackages))

	override fun getTopLevelCallableIds(): Set<CallableId> =
		if (!clrActive) hashSetOf(CallableId(clrPkg, Name.identifier(byrefName)))
		else hashSetOf(CallableId(clrPkg, Name.identifier(byrefName)), CallableId(clrPkg, Name.identifier(stackBufferName))) + topLevelByCallable.keys + topLevelPropByCallable.keys

	override fun getTopLevelClassIds(): Set<ClassId> =
		if (!clrActive) hashSetOf(clrRefClassId) else byClassId.keys + clrRefClassId + stackBufferClassId + spanClassId + clrEventClassId

	override fun generateTopLevelClassLikeDeclaration(classId: ClassId): FirClassLikeSymbol<*>? {
		// The intrinsic `ClrRef<T>` carries getValue/setValue (so a ref return is `by`-delegatable). `ClrEvent<T>`
		// (the .NET-event handle) carries plusAssign/minusAssign; both are generic single-param `kotlin.clr` fictions.
		if (classId == clrRefClassId || classId == stackBufferClassId || classId == spanClassId || classId == clrEventClassId) return createTopLevelClass(classId, ClrGeneratedKey, ClassKind.CLASS) {
			typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
		}.symbol
		val type = byClassId[classId] ?: return null
		val kind = when { type.isAnnotation -> ClassKind.ANNOTATION_CLASS; type.isObject -> ClassKind.OBJECT; type.isInterface -> ClassKind.INTERFACE; else -> ClassKind.CLASS }
		// A non-sealed .NET class is `open` so Kotlin can inherit it (the basis of framework-direct UI).
		val klass = createTopLevelClass(classId, ClrGeneratedKey, kind) {
			if (type.open || type.isInterface) modality = Modality.OPEN
			// round-trip: restore the Kotlin `sealed` modality (a DotKt sealed type lowered to a CLR abstract-class/
			// interface). The closed inheritor set isn't carried, so cross-module exhaustive `when` still needs `else`.
			if (type.isSealed) modality = Modality.SEALED
			// round-trip: a Kotlin `fun interface` (SAM) — restore `status.isFun` so a consumer can pass a lambda where
			// this interface is expected (FIR SAM resolution keys off isFun + the single abstract method facadegen emits).
			if (type.isFunInterface) status { isFun = true }
			// Generic .NET type (`Collection<T>`) -> declare its type parameters. gap ①: restore declaration-site variance
			// (`out`/`in`, interfaces) and upper bound(s) (`<T : Comparable<T>>`) that facadegen now reads back (else invariant/unbounded).
			for (tp in type.typeParams) {
				val variance = when (tp.variance) {
					"out" -> org.jetbrains.kotlin.types.Variance.OUT_VARIANCE
					"in" -> org.jetbrains.kotlin.types.Variance.IN_VARIANCE
					else -> org.jetbrains.kotlin.types.Variance.INVARIANT
				}
				typeParameter(Name.identifier(tp.name), variance, false, ClrGeneratedKey) {
					for (b in tp.bounds)
						bound { tps -> boundConeOf(b, tps) ?: session.builtinTypes.nullableAnyType.coneType }
				}
			}
			// Supertypes: a class's base (`Button` -> `Widget`, for assignability + inherited/protected members),
			// and an interface's GENERIC base interfaces (`IList<T>` -> `ICollection<T>`, so inherited members like
			// `Add` surface — item 3). A spec is either a simple name or `generic:Open[arg,arg]` (args are the owner's
			// type params, resolved against `tps` below). Deferred provider form -> lazy cross-generation.
			for (spec in type.superTypes) {
				val scid = superClassId(spec) ?: continue
				superType { tps -> superTypeCone(spec, scid, tps) }
			}
		}
		// A class with STATIC members: create its companion EAGERLY and LINK it (`replaceCompanionObjectSymbol`), so the
		// IMPLICIT form `App.Start(...)` resolves — the resolver types the bare qualifier off that link (upstream
		// ResolveUtils.kt:457 `typeForQualifierByDeclaration` -> `canBeValue`), and nothing in stock K2 sets it for a
		// fully-generated owner (FirCompanionGenerationProcessor only walks FirFiles). The link makes the framework's
		// nested-generation fallback unreachable (FirGeneratedScopes.kt:245-248 early-returns the linked companion before
		// the :255 `ownerGenerator` assignment), and generated-origin member lookup dies on `ownerGenerator!!`
		// (FirGeneratedScopes.kt:290) — so we set that attribute ourselves via [FirInternals]. The instance is cached:
		// generateNestedClassLikeDeclaration must return THIS companion (one symbol per ClassId, never a second one).
		if (type.hasStatics) {
			val companion = createCompanionObject(klass.symbol, ClrGeneratedKey)
			FirInternals.setOwnerGenerator(companion, this)
			eagerCompanions[companion.symbol.classId] = companion
			klass.replaceCompanionObjectSymbol(companion.symbol)
		}
		return klass.symbol
	}

	/** The owner ClrType of a companion symbol (its static members live here), or null if not a companion-with-statics. */
	private fun companionOwnerType(classId: ClassId): ClrType? =
		if (classId.shortClassName == SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT)
			classId.outerClassId?.let { byClassId[it] }?.takeIf { it.hasStatics }
		else null

	// A normal class with public STATIC members gets a synthesized companion object holding them, so `App.Start(..)`/
	// `App.Current` resolve (Kotlin has no bare statics). The backend emits .NET static calls for these.
	override fun getNestedClassifiersNames(classSymbol: FirClassSymbol<*>, context: NestedClassGenerationContext): Set<Name> {
		val type = byClassId[classSymbol.classId] ?: return emptySet()
		return if (type.hasStatics)
			setOf(SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT) else emptySet()
	}

	override fun generateNestedClassLikeDeclaration(owner: FirClassSymbol<*>, name: Name, context: NestedClassGenerationContext): FirClassLikeSymbol<*>? {
		if (name != SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT) return null
		// Same-instance invariant: the eager companion built (and linked) in generateTopLevelClassLikeDeclaration is
		// THE companion for this ClassId — never create a second one. (Reached only if the framework's early return on
		// the linked companionObjectSymbol — FirGeneratedScopes.kt:245-248 — didn't already answer the lookup.)
		eagerCompanions[owner.classId.createNestedClassId(name)]?.let { return it.symbol }
		val type = byClassId[owner.classId] ?: return null
		if (!type.hasStatics) return null
		return createCompanionObject(owner, ClrGeneratedKey).symbol
	}

	override fun getCallableNamesForClass(classSymbol: FirClassSymbol<*>, context: MemberGenerationContext): Set<Name> {
		// `ClrRef<T>` exposes getValue/setValue so a ref return is `by`-delegatable (`var x by byref(m())`).
		if (classSymbol.classId == clrRefClassId) return hashSetOf(Name.identifier("getValue"), Name.identifier("setValue"))
		// `StackBuffer<T>`: size (val), get/set (operators), asSpan (-> Span<T> = the real System.Span<T>).
		if (classSymbol.classId == stackBufferClassId)
			return hashSetOf(Name.identifier("size"), Name.identifier("get"), Name.identifier("set"), Name.identifier("asSpan"))
		// `ClrEvent<T>`: the `+=`/`-=` subscription operators (member operators — see Codex-verified resolution).
		if (classSymbol.classId == clrEventClassId)
			return hashSetOf(Name.identifier("plusAssign"), Name.identifier("minusAssign"))
		// A companion object: its callables are the owner class's static methods/props.
		companionOwnerType(classSymbol.classId)?.let { ct ->
			val n = HashSet<Name>()
			ct.staticMethods.forEach { n.add(Name.identifier(it.name)) }
			ct.staticProps.forEach { n.add(Name.identifier(it.name)) }
			ct.staticEvents.forEach { n.add(Name.identifier(it.name)) }   // (N6) static .NET event -> companion ClrEvent<T> property
			return n
		}
		val type = byClassId[classSymbol.classId] ?: return emptySet()
		val names = type.methods.mapTo(HashSet()) { Name.identifier(it.name) }
		type.properties.forEach { names.add(Name.identifier(it.name)) }
		type.memberExtProps.forEach { names.add(Name.identifier(it.name)) }
		// A .NET event is surfaced as a read-only member PROPERTY `Changed: ClrEvent<HandlerFn>` (the idiomatic
		// `w.Changed += handler` subscription); the old `add_<E>`/`remove_<E>` accessor-method synthesis is retired.
		type.events.forEach { names.add(Name.identifier(it.name)) }
		type.indexer?.let { names.add(Name.identifier("get")); if (it.mutable) names.add(Name.identifier("set")) }
		if (type.iteratorElem != null) names.add(Name.identifier("iterator"))
		if (!type.isObject && !type.isInterface) names.add(SpecialNames.INIT)   // signals generateConstructors
		return names
	}

	override fun generateProperties(callableId: CallableId, context: MemberGenerationContext?): List<FirPropertySymbol> {
		// DotKt round-trip: a restored top-level property. An EXTENSION property (`val T.p`, non-empty `receiver`) —
		// no owner; the backend routes `x.p` to the file class's get_/set_<p>(__self) statics. A plain NON-extension
		// property (`val greeting`, empty `receiver`) has no extension receiver: the backend routes reads/writes to a
		// STATIC FIELD of the referenced .NET file class (#34b). `isVar = tp.mutable` (rw -> var, ro -> val).
		topLevelPropByCallable[callableId]?.let { tp ->
			return listOf(createTopLevelProperty(ClrGeneratedKey, callableId, coneOf(tp.type, null), !tp.mutable, false) {
				tp.receiver?.let { extensionReceiverType(coneOf(it, null)) }
			}.symbol)
		}
		val owner = context?.owner ?: return emptyList()
		// `StackBuffer<T>.size: Int` (the element count).
		if (owner.classId == stackBufferClassId && callableId.callableName.asString() == "size")
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.intType.coneType, true, false).symbol)
		// A companion object holds the owner class's STATIC props/fields (App.Current). Backend emits .NET static get.
		companionOwnerType(owner.classId)?.let { ct ->
			// (N6) A STATIC .NET event surfaced as a companion read-only `ClrEvent<HandlerFn>` property: `Type.Event += h`
			// reads it (recv = the companion -> clrPropGet static=true), which bir2cir binds to the STATIC add/remove accessor.
			ct.staticEvents.firstOrNull { it.name == callableId.callableName.asString() }?.let { ev ->
				val handler = coneFunctionType(ev.handlerParams.map { coneOf(it.type, owner) }, coneOf(ev.handlerReturn, owner))
				return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, clrEventOf(handler), true, false).symbol)
			}
			val sp = ct.staticProps.firstOrNull { it.name == callableId.callableName.asString() } ?: return emptyList()
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, coneOf(sp.type, owner), !sp.mutable, false).symbol)
		}
		val type = byClassId[owner.classId] ?: return emptyList()
		// A MEMBER extension property (`class C { val T.p }`): a member property of C with an extension receiver; the
		// backend routes `x.p` (inside `with(c)`) to C's get_/set_<p>(__self) method (dispatch on C, receiver as __self).
		type.memberExtProps.firstOrNull { it.name == callableId.callableName.asString() }?.let { mp ->
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, coneOf(mp.type, owner), !mp.mutable, false) {
				extensionReceiverType(coneOf(mp.receiver, owner))
				if (mp.protected) visibility = Visibilities.Protected
			}.symbol)
		}
		// A .NET event -> a read-only member property `Changed: ClrEvent<HandlerFn>` (the `+=`/`-=` subscription handle).
		// No .NET property/field backs it: `w.Changed` is a compile-time handle whose read the backend emits as a plain
		// clrPropGet(owner .NET type, event name) — consumed by bir2cir's ClrEventOperatorBinding, never materialized. The
		// ClrEvent type arg is the handler's Kotlin FUNCTION type, so a lambda `{ s, e -> }` binds straight to `plusAssign(T)`.
		type.events.firstOrNull { it.name == callableId.callableName.asString() }?.let { ev ->
			val handler = coneFunctionType(ev.handlerParams.map { coneOf(it.type, owner) }, coneOf(ev.handlerReturn, owner))
			// A .NET event surfaces as a generated `ClrEvent<T>` handle property (never a real .NET property/field: the
			// read emits a clrPropGet the ClrEventOperatorBinding consumes). Both class instance events and INTERFACE
			// events (facadegen `event`) reach here, and it is emitted NON-abstract even on an interface: an abstract
			// event member would impose an unsatisfiable member obligation on a Kotlin class subclassing a .NET class
			// that implements the interface (`class MyApp : Avalonia.Application` — the ClrEvent<T> is a fiction, not a
			// real overridable property). Non-abstract keeps `x.PropertyChanged += h` resolving on an interface-typed
			// receiver while leaving no obligation; the inherited ClrEvent fake-override is elided by isClrEventProperty.
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, clrEventOf(handler), true, false).symbol)
		}
		val prop = type.properties.firstOrNull { it.name == callableId.callableName.asString() } ?: return emptyList()
		// Property name == .NET name verbatim, so the backend emits `recv.<Name>` directly.
		return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, coneOf(prop.type, owner), !prop.mutable, false) {
			if (type.isInterface || prop.abstract) modality = Modality.ABSTRACT
			else if (prop.open && !type.isObject) modality = Modality.OPEN
			if (prop.protected) visibility = Visibilities.Protected
		}.symbol)
	}

	override fun generateConstructors(context: MemberGenerationContext): List<FirConstructorSymbol> {
		val type = byClassId[context.owner.classId] ?: return emptyList()
		if (type.isObject) return emptyList()
		val ctors = type.ctors.ifEmpty { listOf(emptyList()) }   // a class with no listed ctor still needs one
		return ctors.mapIndexed { i, params ->
			// Only synthesize a `: super()` delegating call when the base actually has a no-arg ctor; a base linked
			// purely for assignability (e.g. WinUI UIElement, SafeHandle) has none, and the façade ctor is never
			// lowered (construction is native newClr) so the missing delegation is harmless.
			val real = realDefaults(params)   // all-buildable ctor defaults -> real defaults (`Pt(y = 4)` omits x); else required
			createConstructor(context.owner, ClrGeneratedKey, i == 0, type.baseNoArgCtor) {
				for (p in params) valueParameter(Name.identifier(p.name), coneOf(p.type, context.owner), hasDefaultValue = real && p.default != null)
			}.also { if (real) applyDefaults(it, params) }.symbol
		}
	}

	override fun generateFunctions(callableId: CallableId, context: MemberGenerationContext?): List<FirNamedFunctionSymbol> {
		val owner = context?.owner
		if (owner == null) {
			// Top-level intrinsic `byref`: `fun <T> byref(x: T): ClrRef<T>` (marks a call arg as a .NET out/ref param).
			if (callableId.callableName.asString() == byrefName) {
				val fn = createTopLevelFunction(ClrGeneratedKey, callableId,
					{ tps -> clrRefOf(tps[0].symbol.constructType(emptyArray(), false)) }) {
					typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					valueParameter(Name.identifier("x"), { tps -> tps[0].symbol.constructType(emptyArray(), false) })
				}
				return listOf(fn.symbol)
			}
			// `inline fun <T, R> stackBuffer(n: Int, block: (StackBuffer<T>) -> R): R` — scoped stack allocation.
			if (callableId.callableName.asString() == stackBufferName) {
				val fn = createTopLevelFunction(ClrGeneratedKey, callableId,
					{ tps -> tps[1].symbol.constructType(emptyArray(), false) }) {
					typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					typeParameter(Name.identifier("R"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					valueParameter(Name.identifier("n"), session.builtinTypes.intType.coneType)
					valueParameter(Name.identifier("block"), { tps ->
						coneFunctionType(listOf(stackBufferOf(tps[0].symbol.constructType(emptyArray(), false))), tps[1].symbol.constructType(emptyArray(), false))
					})
				}
				return listOf(fn.symbol)
			}
			// DotKt round-trip: top-level functions restored from a [KotlinFile] facade. infix/operator are member-only,
			// so a top-level fun carries at most `suspend`; the backend emits the static call.
			topLevelByCallable[callableId]?.let { tls ->
				return tls.flatMap { tl ->
					val m = tl.fn
					// An extension fun: the first param `__self` is the receiver (rest are value params).
					val extRecv = if (m.ext && m.params.isNotEmpty()) m.params[0] else null
					val vps = if (extRecv != null) m.params.drop(1) else m.params
					val real = realDefaults(vps)
					val trailingOpt = if (real) 0 else vps.reversed().takeWhile { it.default != null }.count()
					((vps.size - trailingOpt)..vps.size).map { arity ->
						createTopLevelFunction(ClrGeneratedKey, callableId, { tps -> coneOf(m.returnType, null, tps) }) {
							for (tp in m.typeParams) typeParameter(Name.identifier(tp.name), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey) {
								for (b in tp.bounds)   // gap ①: `<T : Comparable<T>>` bound on a top-level fun
									bound { tps -> boundConeOf(b, tps) ?: session.builtinTypes.nullableAnyType.coneType }
							}
							if (m.suspend) status { isSuspend = true }
							if (m.inline) status { isInline = true }   // accept non-local return; ilemit splices the carried body
							if (m.infix || m.operator) status { isInfix = m.infix; isOperator = m.operator }   // top-level extension operators
							if (extRecv != null) extensionReceiverType { tps -> coneOf(extRecv.type, null, tps) }
							for (p in vps.take(arity))
								if (p.vararg) valueParameter(Name.identifier(p.name), { tps -> coneOf(TypeNode.Array(p.type), null, tps) }, isVararg = true)
								else valueParameter(Name.identifier(p.name), { tps -> coneOf(p.type, null, tps) }, hasDefaultValue = real && p.default != null)
						}.also { if (real) applyDefaults(it, vps) }.symbol
					}
				}
			}
			return emptyList()
		}
		// `StackBuffer<T>` size/get/set: size is a property (generateProperties); get/set are indexing operators.
		if (owner.classId == stackBufferClassId) {
			val tOf = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
			val intT = session.builtinTypes.intType.coneType
			val fn = when (callableId.callableName.asString()) {
				"get" -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, tOf) {
					status { isOperator = true }; valueParameter(Name.identifier("index"), intT)
				}
				"asSpan" -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, spanOf(tOf)) {}
				else -> createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.unitType.coneType) {
					status { isOperator = true }; valueParameter(Name.identifier("index"), intT); valueParameter(Name.identifier("value"), tOf)
				}
			}
			return listOf(fn.symbol)
		}
		// `ClrRef<T>` operator getValue/setValue (a managed-reference `by`-delegate). The backend inlines them to
		// ldobj/stobj on the stored byref local; here they only need to type-check the `var x by byref(m())` form.
		if (owner.classId == clrRefClassId) {
			val tOf = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
			val anyN = session.builtinTypes.nullableAnyType.coneType
			val kProp = session.symbolProvider.getClassLikeSymbolByClassId(ClassId(FqName("kotlin.reflect"), Name.identifier("KProperty")))
				?.constructType(arrayOf(org.jetbrains.kotlin.fir.types.ConeStarProjection), false) ?: anyN
			val fn = if (callableId.callableName.asString() == "getValue")
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, tOf) {
					status { isOperator = true }
					valueParameter(Name.identifier("thisRef"), anyN); valueParameter(Name.identifier("property"), kProp)
				}
			else createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.unitType.coneType) {
				status { isOperator = true }
				valueParameter(Name.identifier("thisRef"), anyN); valueParameter(Name.identifier("property"), kProp)
				valueParameter(Name.identifier("value"), tOf)
			}
			return listOf(fn.symbol)
		}
		// `ClrEvent<T>` subscription operators: `operator fun plusAssign(handler: T): Unit` / `minusAssign(handler: T)`.
		// No body (never executed) — bir2cir's ClrEventOperatorBinding rewrites `w.Changed.plusAssign(h)` to the .NET
		// add/remove accessor node (clrEventAdd/clrEventRemove) before emit. `operator` is REQUIRED for `+=`/`-=` to resolve.
		if (owner.classId == clrEventClassId) {
			val tOf = owner.typeParameterSymbols.first().constructType(emptyArray(), false)
			val fn = createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.unitType.coneType) {
				status { isOperator = true }
				valueParameter(Name.identifier("handler"), tOf)
			}
			return listOf(fn.symbol)
		}
		// A companion object holds the owner class's STATIC methods (App.Start(..)). The backend emits .NET static calls.
		companionOwnerType(owner.classId)?.let { ct ->
			val cn = callableId.callableName.asString()
			return ct.staticMethods.filter { it.name == cn }.map { m ->
				if (m.typeParams.isEmpty())
					createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, coneOf(m.returnType, owner)) {
						for (p in m.params) valueParameter(Name.identifier(p.name), coneOf(p.type, owner))
					}.symbol
				else
					// A GENERIC static (`Task.FromResult<TResult>(TResult): Task<TResult>`, `Task.Run<TResult>`): declare the
					// method's own type parameters, then resolve the return type and any T-typed params against THEM (via the
					// provider forms — the params don't exist until the function is being built), like the generic instance path.
					// This is the seam that lets Kotlin BUILD a Task<T> from a .NET generic factory (async interop).
					createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
						{ tps -> coneOf(m.returnType, owner, tps) }) {
						for (tp in m.typeParams) typeParameter(Name.identifier(tp.name), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
						// N3-deep: a GENERIC static's `vararg` param (`Task.WhenAll<T>(params Task<T>[])`) rebuilds as an
						// `Array<elem>` vararg so it resolves to the real `params Task<T>[]` overload (not `Any?`).
						for (p in m.params)
							if (p.vararg) valueParameter(Name.identifier(p.name), { tps -> coneOf(TypeNode.Array(p.type), owner, tps) }, isVararg = true)
							else valueParameter(Name.identifier(p.name), { tps -> coneOf(p.type, owner, tps) })
					}.symbol
			}
		}
		val type = byClassId[owner.classId] ?: return emptyList()
		val callName = callableId.callableName.asString()

		// Indexer `this[i]` -> `operator fun get(index): V` / `operator fun set(index, value): Unit`.
		val ix = type.indexer
		if (ix != null && (callName == "get" || callName == "set")) {
			val fn = createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
				if (callName == "get") coneOf(ix.valueType, owner) else session.builtinTypes.unitType.coneType) {
				status { isOperator = true }
				if (type.open && !type.isObject) modality = Modality.OPEN
				valueParameter(Name.identifier("index"), coneOf(ix.indexType, owner))
				if (callName == "set") valueParameter(Name.identifier("value"), coneOf(ix.valueType, owner))
			}
			return listOf(fn.symbol)
		}

		// IEnumerable<T> -> `operator fun iterator(): Iterator<T>`. Frontend-only: it lets `for (x in it)` resolve to a
		// single member (not the clashing stdlib extension iterator()s); the backend bypasses it and enumerates via
		// GetEnumerator/MoveNext/Current (see BirEmitter forEachInline).
		if (callName == "iterator" && type.iteratorElem != null) {
			val iterCid = ClassId(FqName("kotlin.collections"), Name.identifier("Iterator"))
			val iterSym = session.symbolProvider.getClassLikeSymbolByClassId(iterCid)
			val ret = iterSym?.constructType(arrayOf(coneOf(type.iteratorElem!!, owner)), false)
				?: session.builtinTypes.nullableAnyType.coneType
			val fn = createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, ret) {
				status { isOperator = true }
				// On the injected `IEnumerable<T>` INTERFACE itself the member is abstract (interfaces carry no body);
				// derived interfaces (IList<T>/IReadOnlyList<T>/...) inherit it, so `for (x in ilist)` resolves too.
				if (type.isInterface) modality = Modality.ABSTRACT
				else if (type.open && !type.isObject) modality = Modality.OPEN
			}
			return listOf(fn.symbol)
		}

		val overloads = type.methods.filter { it.name == callName }
		// Member name == .NET name verbatim, so the backend emits the call as-is (no per-member map).
		return overloads.flatMap { m ->
			// A MEMBER extension function (`class C { fun T.f() }`): the first param is `__self` (the extension receiver,
			// marked `,ext`), restored as a Kotlin extension receiver; the rest are value params. Composes with
			// generic / infix / operator / suspend / inline / protected — the full "hellish member" cross-product.
			val extRecv = if (m.ext && m.params.isNotEmpty()) m.params[0] else null
			val vps = if (extRecv != null) m.params.drop(1) else m.params
			if (m.typeParams.isEmpty()) {
				// Default args: ONE function with REAL constant default values (applyDefaults), so the consumer can omit a
				// default arg ANYWHERE (trailing, named-middle `f(c=9)`, reordered); fir2ir inlines the literal.
				val real = realDefaults(vps)
				val trailingOpt = if (real) 0 else vps.reversed().takeWhile { it.default != null }.count()
				((vps.size - trailingOpt)..vps.size).map { arity ->
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, coneOf(m.returnType, owner)) {
					// interface members + .NET abstract => ABSTRACT (must implement); .NET virtual => OPEN (overridable).
					if (type.isInterface || m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected   // overridable protected lifecycle methods (item 2)
					// DotKt round-trip: Kotlin modifiers with no .NET analog, restored from the `mods` object.
					if (m.infix || m.operator || m.suspend) status { isInfix = m.infix; isOperator = m.operator; isSuspend = m.suspend }
					if (m.inline) status { isInline = true }
					if (extRecv != null) extensionReceiverType(coneOf(extRecv.type, owner))
					for (p in vps.take(arity))
						if (p.vararg) valueParameter(Name.identifier(p.name), coneOf(TypeNode.Array(p.type), owner), isVararg = true)
						else valueParameter(Name.identifier(p.name), coneOf(p.type, owner), hasDefaultValue = real && p.default != null)
				}.also { if (real) applyDefaults(it, vps) }.symbol
				}
			} else listOf(
				// A generic .NET method (`SizeOf<T>()`, `As<T>(o): T`). Declare its method type parameters, then resolve
				// the return type and any T-typed param/receiver against THOSE params (via the provider forms). The CLR
				// has reified generics, so the backend just emits a generic .NET method call (MakeGenericMethod).
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
					{ tps -> coneOf(m.returnType, owner, tps) }) {
					if (m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected
					if (m.infix || m.operator || m.suspend) status { isInfix = m.infix; isOperator = m.operator; isSuspend = m.suspend }
					if (m.inline) status { isInline = true }
					for (tp in m.typeParams) typeParameter(Name.identifier(tp.name), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey) {
						for (b in tp.bounds)   // gap ①: `<T : Comparable<T>>` bound on a member fun
							bound { tps -> boundConeOf(b, tps) ?: session.builtinTypes.nullableAnyType.coneType }
					}
					if (extRecv != null) extensionReceiverType { tps -> coneOf(extRecv.type, owner, tps) }
					// N3-deep: a GENERIC member method's `vararg` param rebuilds as an `Array<elem>` vararg so it resolves.
					for (p in vps)
						if (p.vararg) valueParameter(Name.identifier(p.name), { tps -> coneOf(TypeNode.Array(p.type), owner, tps) }, isVararg = true)
						else valueParameter(Name.identifier(p.name), { tps -> coneOf(p.type, owner, tps) }, hasDefaultValue = realDefaults(vps) && p.default != null)
				}.also { if (realDefaults(vps)) applyDefaults(it, vps) }.symbol
			)
		}
	}

	/** The ClassId of a supertype `spec` (a `Fqn`) — a fully-qualified `pkg.Name` or a (simpleName, arity) injected type. */
	private fun superClassId(spec: TypeNode): ClassId? {
		val f = spec as? TypeNode.Fqn ?: return null
		val arity = f.args?.size ?: 0
		return if ('.' in f.name) ClassId(FqName(f.name.substringBeforeLast('.')), Name.identifier(f.name.substringAfterLast('.')))
			else ClrMetadataHolder.classIdFor(f.name, arity)
	}

	/** Resolve a supertype spec (a `Fqn`, optionally with args) to a ConeKotlinType, resolving arg type-variables against
	 *  the owner's own type parameters (`tps`, available in the class-builder superType form). */
	private fun superTypeCone(spec: TypeNode, scid: ClassId, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(scid) ?: return session.builtinTypes.anyType.coneType
		val args = (spec as? TypeNode.Fqn)?.args ?: emptyList()
		@Suppress("UNCHECKED_CAST")
		return sym.constructType(args.map { superArgCone(it, tps) }.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>, false)
	}

	/** A supertype type-argument: a `Tv(type,i)` binds to the owner's type parameter (`tps[i]`), else a primitive or
	 *  another injected type built as a LAZY lookup-tag cone. A self-referential supertype (`Money : IComparable<Money>`)
	 *  runs THIS lambda while `Money` is still being built, so the lookup-tag cone (a by-ClassId reference resolved later)
	 *  avoids the re-entrant StackOverflow that resolving the symbol here would cause. */
	private fun superArgCone(node: TypeNode, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		val bt = session.builtinTypes
		return when (node) {
			is TypeNode.Tv -> (if (node.scope == "type") tps.getOrNull(node.i)?.symbol?.constructType(emptyArray(), false) else null) ?: bt.nullableAnyType.coneType
			is TypeNode.Nullable -> superArgCone(node.of, tps).withNullability(true, session.typeContext)
			is TypeNode.Oblivious -> superArgCone(node.of, tps)
			is TypeNode.Fqn -> when (node.name) {
				"Int" -> bt.intType.coneType; "Long" -> bt.longType.coneType; "Double" -> bt.doubleType.coneType
				"Float" -> bt.floatType.coneType; "Short" -> bt.shortType.coneType; "Byte" -> bt.byteType.coneType
				"Boolean" -> bt.booleanType.coneType; "Char" -> bt.charType.coneType; "String" -> bt.stringType.coneType
				else -> {
					val arity = node.args?.size ?: 0
					val cid = if ('.' in node.name) ClassId(FqName(node.name.substringBeforeLast('.')), Name.identifier(node.name.substringAfterLast('.')))
						else if (node.name in UNSIGNED_KOTLIN_TYPES) ClassId(FqName("kotlin"), Name.identifier(node.name))  // #53
						else ClrMetadataHolder.classIdFor(node.name, arity)
					if (cid == null) bt.nullableAnyType.coneType
					else {
						@Suppress("UNCHECKED_CAST")
						val args = (node.args?.map { superArgCone(it, tps) } ?: emptyList()).toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
						ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(cid), args, false)
					}
				}
			}
			else -> bt.nullableAnyType.coneType
		}
	}

	// gap ①: kotlin BUILTIN types that appear as generic-constraint targets but aren't in the injection metadata (the
	// jar/frontend owns them). The common one: a Kotlin `T : Comparable<T>` bound lowers to CLR `System.IComparable<T>`,
	// which facadegen reverses to `Fqn("Comparable",[T])` (MapBoundT). Resolve that back to the real kotlin.Comparable symbol.
	private val builtinBoundOpen: Map<String, ClassId> = mapOf(
		"Comparable" to ClassId(FqName("kotlin"), Name.identifier("Comparable")),
		"Number" to ClassId(FqName("kotlin"), Name.identifier("Number")),
		"Enum" to ClassId(FqName("kotlin"), Name.identifier("Enum")),
		"CharSequence" to ClassId(FqName("kotlin"), Name.identifier("CharSequence")),
	)

	/** gap ①: the ClassId of a bound's OPEN class — an INJECTED type (by name+arity, or a dotted ClassId), or a
	 *  well-known kotlin builtin. Null => the caller drops the bound (unconstrained `T`). Returns a ClassId (the caller
	 *  builds a LAZY lookup-tag cone), never a resolved symbol. */
	private fun boundClassId(open: String, arity: Int): ClassId? =
		ClrMetadataHolder.classIdFor(open, arity)
			?: if ('.' in open) ClassId(FqName(open.substringBeforeLast('.')), Name.identifier(open.substringAfterLast('.'))) else builtinBoundOpen[open]

	/** gap ①: resolve a generic-constraint bound TypeNode (`Fqn("Comparable",[T])`, an injected type, or a `Tv`) to a
	 *  cone, binding a self-referential arg (`Tv`) to the declaring type/function's own type parameters (`tps`). Null =>
	 *  unresolvable (the caller falls back to `Any?`). The open class is a LAZY lookup-tag cone (never resolves the symbol
	 *  eagerly — the curiously-recurring BCL bounds reference types STILL BEING BUILT). Fail-soft: never a crash. */
	private fun boundConeOf(node: TypeNode, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType? {
		try {
			return when (node) {
				is TypeNode.Tv -> tps.getOrNull(node.i)?.symbol?.constructType(emptyArray(), false)
				is TypeNode.Nullable -> boundConeOf(node.of, tps)?.withNullability(true, session.typeContext)
				is TypeNode.Oblivious -> boundConeOf(node.of, tps)
				is TypeNode.Fqn -> {
					val cid = boundClassId(node.name, node.args?.size ?: 0) ?: return null
					@Suppress("UNCHECKED_CAST")
					val args = (node.args?.map { boundConeOf(it, tps) ?: session.builtinTypes.nullableAnyType.coneType } ?: emptyList())
						.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
					ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(cid), args, false)
				}
				else -> null
			}
		} catch (e: Throwable) { return null }
	}

	/** A Kotlin function type `(P...) -> R` = `kotlin.FunctionN<P..., R>`, for event handler params. */
	private fun coneFunctionType(params: List<ConeKotlinType>, ret: ConeKotlinType): ConeKotlinType {
		val cid = ClassId(FqName("kotlin"), Name.identifier("Function${params.size}"))
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return session.builtinTypes.nullableAnyType.coneType
		@Suppress("UNCHECKED_CAST")
		val args = (params + ret).toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
		return sym.constructType(args, false)
	}

	/** H2: a Kotlin SUSPEND function type `suspend (P...) -> R` = `kotlin.coroutines.SuspendFunctionN<P..., R>`. The
	 *  built-in function-kind symbol provider synthesizes these classIds (like `kotlin.FunctionN`), and the
	 *  FunctionTypeKindExtractor recognizes the `kotlin.coroutines` + `SuspendFunction` prefix as the SuspendFunction
	 *  kind — so a param restored with this type makes a passed lambda a SUSPEND lambda (overload resolution and
	 *  inference treat it as suspend, not plain `Func`). facadegen emits the `sfunc:[ret,args]` meta from the DotKt
	 *  assembly's KotlinSuspendFunctionType attribute, restoring what bir2cir erased to `object` in the CLR signature.
	 *  Falls back to a plain function type if the synthetic symbol can't be resolved (never a crash). */
	private fun coneSuspendFunctionType(params: List<ConeKotlinType>, ret: ConeKotlinType): ConeKotlinType {
		val cid = ClassId(FqName("kotlin.coroutines"), Name.identifier("SuspendFunction${params.size}"))
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return coneFunctionType(params, ret)
		@Suppress("UNCHECKED_CAST")
		val args = (params + ret).toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
		return sym.constructType(args, false)
	}

	/** Map a metadata type name to a ConeKotlinType: primitives, the owner itself, another injected type, else Any?. */
	/** The intrinsic `ClrRef<arg>` cone type (the surfaced form of a .NET out/ref param or ref return). */
	private fun clrRefOf(arg: ConeKotlinType): ConeKotlinType =
		session.symbolProvider.getClassLikeSymbolByClassId(clrRefClassId)?.constructType(arrayOf(arg), false)
			?: session.builtinTypes.nullableAnyType.coneType

	/** The intrinsic `StackBuffer<arg>` cone type (the block parameter of `stackBuffer`). */
	private fun stackBufferOf(arg: ConeKotlinType): ConeKotlinType =
		session.symbolProvider.getClassLikeSymbolByClassId(stackBufferClassId)?.constructType(arrayOf(arg), false)
			?: session.builtinTypes.nullableAnyType.coneType

	/** The intrinsic `Span<arg>` cone type (-> the real System.Span<arg>). */
	private fun spanOf(arg: ConeKotlinType): ConeKotlinType =
		session.symbolProvider.getClassLikeSymbolByClassId(spanClassId)?.constructType(arrayOf(arg), false)
			?: session.builtinTypes.nullableAnyType.coneType

	/** The intrinsic `ClrEvent<handler>` cone type (the surfaced form of a .NET event; `handler` = the handler's
	 *  Kotlin function type, so a lambda binds to `plusAssign(handler: T)`). Never materialized — a compile-time handle. */
	private fun clrEventOf(handler: ConeKotlinType): ConeKotlinType =
		session.symbolProvider.getClassLikeSymbolByClassId(clrEventClassId)?.constructType(arrayOf(handler), false)
			?: session.builtinTypes.nullableAnyType.coneType

	/** `ClrDefault` -> a `FirLiteralExpression`, so a restored default arg has a REAL constant value fir2ir can inline at
	 *  the call site (the consumer may omit it ANYWHERE — trailing, named-middle `f(c=9)`, or reordered). A `null` value =>
	 *  the null default; an unbuildable `valueType` (enum/struct) -> null (the caller falls back to @JvmOverloads arities). */
	private fun optDefault(d: ClrDefault?): FirExpression? {
		if (d == null) return null
		if (d.value == null) return buildLiteralExpression(null, ConstantValueKind.Null, null, setType = true)
		val v = d.value
		val (kind, value) = when (d.valueType) {
			"Int" -> ConstantValueKind.Int to (v.toIntOrNull() ?: return null)
			"Long" -> ConstantValueKind.Long to (v.toLongOrNull() ?: return null)
			"Short" -> ConstantValueKind.Short to (v.toShortOrNull() ?: return null)
			"Byte" -> ConstantValueKind.Byte to (v.toByteOrNull() ?: return null)
			"Boolean" -> ConstantValueKind.Boolean to (v == "true")
			"Double" -> ConstantValueKind.Double to (v.toDoubleOrNull() ?: return null)
			"Float" -> ConstantValueKind.Float to (v.toFloatOrNull() ?: return null)
			"Char" -> ConstantValueKind.Char to (v.firstOrNull() ?: return null)
			"String" -> ConstantValueKind.String to v
			else -> return null
		}
		return buildLiteralExpression(null, kind, value, setType = true)
	}

	/** Apply restored constant defaults to a generated function/ctor's value parameters (replaces the fir2ir-crashing
	 *  stub the `hasDefaultValue` flag inserts with a real literal). `params` are the value params in order. */
	private fun applyDefaults(fn: FirFunction, params: List<ClrParam>) {
		fn.valueParameters.forEachIndexed { i, vp -> if (i < params.size) optDefault(params[i].default)?.let { vp.replaceDefaultValue(it) } }
	}

	/** True when EVERY default-arg param has a buildable constant default -> the function/ctor is restored as ONE function
	 *  with real defaults (the consumer may omit ANY default arg). A BCL method with an enum/struct default isn't
	 *  buildable -> @JvmOverloads fallback (trailing-omission arities). The two strategies must not mix on one function. */
	private fun realDefaults(params: List<ClrParam>): Boolean =
		params.all { it.default == null || optDefault(it.default) != null }

	/** Resolve a structured TypeNode (spec §1) to a ConeKotlinType. `methodTps` = the enclosing method's declared type
	 *  parameters, so a `Tv(scope="method", i)` binds to `methodTps[i]`; a `Tv(scope="type", i)` binds to the owner's
	 *  i-th type parameter. There is NO type-string parsing — every case dispatches on the node kind. */
	private fun coneOf(node: TypeNode, owner: FirClassSymbol<*>?, methodTps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>? = null): ConeKotlinType {
		val bt = session.builtinTypes
		return when (node) {
			// `T?` (nullable) and `T!` (oblivious/flexible platform `(T..T?)` — the frontend decides null-safety per use).
			is TypeNode.Nullable -> coneOf(node.of, owner, methodTps).withNullability(true, session.typeContext)
			is TypeNode.Oblivious -> {
				val lower = coneOf(node.of, owner, methodTps)
				val upper = lower.withNullability(true, session.typeContext)
				if (lower is ConeRigidType && upper is ConeRigidType) ConeFlexibleType(lower, upper, false) else lower
			}
			// A .NET out/ref param / ref return -> the intrinsic `ClrRef<T>`.
			is TypeNode.ByRef -> clrRefOf(coneOf(node.of, owner, methodTps))
			// A .NET array -> Kotlin `Array<T>` / a primitive `IntArray`/etc.
			is TypeNode.Array -> {
				val prim = (node.elem as? TypeNode.Fqn)?.takeIf { it.args == null }?.name?.let {
					mapOf("Int" to "IntArray", "Long" to "LongArray", "Double" to "DoubleArray", "Float" to "FloatArray",
						"Short" to "ShortArray", "Byte" to "ByteArray", "Boolean" to "BooleanArray", "Char" to "CharArray",
						// A .NET unsigned-element array -> Kotlin's SPECIALIZED unsigned primitive array (#53): System.Byte[]
						// -> UByteArray (NOT Array<UByte>), consistent with the signed primitive arrays above.
						"UByte" to "UByteArray", "UShort" to "UShortArray", "UInt" to "UIntArray", "ULong" to "ULongArray")[it]
				}
				val cid = if (prim != null) ClassId(FqName("kotlin"), Name.identifier(prim)) else ClassId(FqName("kotlin"), Name.identifier("Array"))
				val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return bt.nullableAnyType.coneType
				sym.constructType(if (prim != null) emptyArray() else arrayOf(coneOf(node.elem, owner, methodTps)), false)
			}
			// A .NET delegate / a `suspend (…) -> T` position -> a Kotlin (suspend) function type, so a lambda binds and
			// overloads disambiguate. A suspend fn makes a passed lambda a SUSPEND lambda (the H2 round-trip).
			is TypeNode.Fn ->
				if (node.suspend) coneSuspendFunctionType(node.params.map { coneOf(it, owner, methodTps) }, coneOf(node.ret, owner, methodTps))
				else coneFunctionType(node.params.map { coneOf(it, owner, methodTps) }, coneOf(node.ret, owner, methodTps))
			// A positional type variable: scope "method" -> the method's own type param `methodTps[i]`; scope "type" ->
			// the owner's i-th type parameter. (The `gp:`-name remap is gone — spec §1.)
			is TypeNode.Tv -> {
				val sym = if (node.scope == "method") methodTps?.getOrNull(node.i)?.symbol
					else owner?.typeParameterSymbols?.getOrNull(node.i)
				sym?.constructType(emptyArray(), false) ?: bt.nullableAnyType.coneType
			}
			// A named type: a primitive/builtin, else an injected type by (simpleName, arity) or a dotted `pkg.Name`.
			is TypeNode.Fqn -> {
				if (node.args == null) when (node.name) {
					"Int" -> return bt.intType.coneType; "Long" -> return bt.longType.coneType
					"Double" -> return bt.doubleType.coneType; "Float" -> return bt.floatType.coneType
					"Short" -> return bt.shortType.coneType; "Byte" -> return bt.byteType.coneType
					"Boolean" -> return bt.booleanType.coneType; "Char" -> return bt.charType.coneType
					"String" -> return bt.stringType.coneType; "Unit" -> return bt.unitType.coneType
				}
				val arity = node.args?.size ?: 0
				val cid = if ('.' in node.name) ClassId(FqName(node.name.substringBeforeLast('.')), Name.identifier(node.name.substringAfterLast('.')))
					// Unsigned scalar/array types have no `bt.*` builtin (they are library types in the `kotlin` package);
					// resolve them straight off the symbol provider. WITHOUT this a facadegen-injected System.Byte->UByte
					// RETURN degrades to Any? (#53 — a PARAM position tolerated the degrade, a return does not).
					else if (node.name in UNSIGNED_KOTLIN_TYPES) ClassId(FqName("kotlin"), Name.identifier(node.name))
					else ClrMetadataHolder.classIdFor(node.name, arity)
				val sym = when { cid == null -> null; cid == owner?.classId -> owner; else -> session.symbolProvider.getClassLikeSymbolByClassId(cid) }
				if (sym == null) bt.nullableAnyType.coneType
				else {
					@Suppress("UNCHECKED_CAST")
					val args = (node.args?.map { coneOf(it, owner, methodTps) } ?: emptyList()).toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>
					sym.constructType(args, false)
				}
			}
		}
	}
}

/** Registers [ClrTypeInjector] as a FIR class-generation extension (supertypes are declared in its class builder). */
class ClrFirExtensionRegistrar : FirExtensionRegistrar() {
	override fun ExtensionRegistrarContext.configurePlugin() {
		+::ClrTypeInjector
	}
}

/**
 * The compiler-plugin entry the pipeline runs against the frontend's project: it installs the FIR
 * registrar. Wired into [kotc.pipeline.ClrCliPipeline] via `COMPILER_PLUGIN_REGISTRARS`.
 */
class ClrCompilerPluginRegistrar : CompilerPluginRegistrar() {
	override val supportsK2: Boolean = true
	override fun ExtensionStorage.registerExtensions(configuration: CompilerConfiguration) {
		FirExtensionRegistrarAdapter.registerExtension(ClrFirExtensionRegistrar())
	}
}
