@file:OptIn(
	org.jetbrains.kotlin.fir.extensions.FirExtensionApiInternals::class,
	org.jetbrains.kotlin.fir.extensions.ExperimentalTopLevelDeclarationsGenerationApi::class,
	org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
)

package clrc.frontend

import clrc.ClrEventRegistry
import clrc.ClrTopLevelRegistry
import clrc.ClrTypeRegistry
import java.io.File
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
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.coneType
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

private class ClrParam(val name: String, val type: String)
// `open`/`abstract` = .NET virtual/abstract (Kotlin OPEN/ABSTRACT modality, overridable); `protected` = .NET
// Family/FamORAssem (so a Kotlin subclass can override a protected virtual lifecycle method — feedback item 2).
// `typeParams` = method-level generic parameters (`SizeOf<T>()` -> ["T"]); empty for ordinary methods.
// `infix`/`operator`/`suspend` = Kotlin modifiers with no .NET analog, restored from a DotKt assembly's
// [KotlinFunction] (carried in the `fun`/`tlfun` modifier token as comma-suffixes). For a `suspend` fun the
// returnType is already the unwrapped result T (facadegen unwrapped the emitted Task<T>).
private class ClrMethod(val name: String, val returnType: String, val open: Boolean, val abstract: Boolean, val protected: Boolean, val params: List<ClrParam>, val typeParams: List<String> = emptyList(),
	val infix: Boolean = false, val operator: Boolean = false, val suspend: Boolean = false, val inline: Boolean = false, val ext: Boolean = false)
// A restored top-level Kotlin function: its package, the .NET file-facade class to call, and the function itself.
private class ClrTopLevel(val pkg: FqName, val fileClassDotNet: String, val fn: ClrMethod)
private class ClrTopLevelProp(val pkg: FqName, val fileClassDotNet: String, val name: String, val type: String, val mutable: Boolean, val receiver: String)
private class ClrProperty(val name: String, val type: String, val mutable: Boolean, val open: Boolean, val abstract: Boolean, val protected: Boolean)
// A MEMBER extension property (`class C { val T.p }`): restored as a member property of C with an extension receiver.
private class ClrMemberExtProp(val name: String, val type: String, val mutable: Boolean, val receiver: String, val protected: Boolean)
private class ClrEvent(val name: String, val handlerReturn: String, val handlerParams: List<ClrParam>)
// A `this[i]` indexer -> Kotlin `operator fun get/set` (`set` only when mutable).
private class ClrIndexer(val indexType: String, val valueType: String, val mutable: Boolean)
private class ClrType(
	val kotlinName: String,
	val dotNetName: String,
	val isObject: Boolean,
	val isInterface: Boolean,          // .NET interface => Kotlin can implement it
	val isAnnotation: Boolean,         // System.Attribute-derived => Kotlin annotation class (apply on decls)
	val open: Boolean,                 // .NET non-sealed => Kotlin can extend it
	val typeParams: List<String>,      // generic type parameter names (`Collection<T>` -> ["T"])
	val superTypes: List<String>,      // injectable base class + interfaces (simple names) — wired by ClrSupertypeInjector
	val methods: List<ClrMethod>,
	val ctors: List<List<ClrParam>>,
	val properties: List<ClrProperty>,
	val events: List<ClrEvent>,
	val indexer: ClrIndexer?,
	val iteratorElem: String?,         // IEnumerable<T> element -> a frontend-only `operator fun iterator(): Iterator<T>`
	val baseNoArgCtor: Boolean,        // false ("basector none"): base lacks a no-arg ctor -> don't synthesize `: super()`
	val staticMethods: List<ClrMethod>,// public static methods of a NORMAL class -> companion-object members (App.Start)
	val staticProps: List<ClrProperty>,// public static props/fields of a NORMAL class -> companion-object members
	val memberExtProps: List<ClrMemberExtProp> = emptyList(),  // `class C { val T.p }` member extension properties
)
private class ClrModule(val types: List<ClrType>, val topLevel: List<ClrTopLevel> = emptyList(), val topLevelProps: List<ClrTopLevelProp> = emptyList())

// Parse a `fun`/`tlfun` modifier token: `[prot-]<open|final|abstract>[,infix][,operator][,suspend]` — a single
// whitespace-free token (so the meta parser's type-param split is unaffected), the flags as comma-suffixes.
private class FunMods(val open: Boolean, val abstract: Boolean, val protected: Boolean, val infix: Boolean, val operator: Boolean, val suspend: Boolean, val inline: Boolean, val ext: Boolean)
private fun parseFunMods(tok: String?): FunMods {
	val parts = (tok ?: "final").split(',')
	val mod = parts[0]; val prot = mod.startsWith("prot-"); val bare = mod.removePrefix("prot-")
	val f = parts.drop(1).toHashSet()
	return FunMods(bare == "open", bare == "abstract", prot, "infix" in f, "operator" in f, "suspend" in f, "inline" in f, "ext" in f)
}

/**
 * Loads the .NET type metadata to inject, once per process. The path comes from `CLR_TYPES_METADATA`
 * (set by the build / MSBuild / verify harness). Absent or empty => inject nothing, so compilations
 * that don't opt in are completely unaffected. Each injected type is also recorded in [ClrTypeRegistry]
 * so the backend maps it to its .NET name.
 */
private object ClrMetadataHolder {
	val module: ClrModule? by lazy { System.getenv("CLR_TYPES_METADATA")?.let { load(File(it)) } }
	// Namespace projections (.NET prefix -> Kotlin prefix), from `nsproj` meta lines. Set during load (before the
	// registration loop), so namespaceOf can map a projected .NET namespace back to the Kotlin package the user imports.
	private var projections: List<Pair<String, String>> = emptyList()
	/** A .NET namespace -> the Kotlin package it's exposed as (`DotKt.Coroutines` -> `kotlinx.coroutines`); identity if unprojected. */
	private fun toKotlinPkg(dotNetNs: String): String {
		for ((dotNet, kotlin) in projections) if (dotNetNs == dotNet || dotNetNs.startsWith("$dotNet.")) return kotlin + dotNetNs.substring(dotNet.length)
		return dotNetNs
	}

	private fun load(file: File): ClrModule? {
		if (!file.isFile) return null
		val types = ArrayList<ClrType>()
		var name = ""; var dotNet = ""; var isObject = false; var isInterface = false; var isOpen = false; var isAnnotation = false
		var tparams = emptyList<String>(); var supers = emptyList<String>()
		val methods = ArrayList<ClrMethod>(); val ctors = ArrayList<List<ClrParam>>()
		val props = ArrayList<ClrProperty>(); val events = ArrayList<ClrEvent>()
		var indexer: ClrIndexer? = null
		var iteratorElem: String? = null
		var baseNoArgCtor = true
		val staticMethods = ArrayList<ClrMethod>(); val staticProps = ArrayList<ClrProperty>()
		val memberExtProps = ArrayList<ClrMemberExtProp>()
		val topLevel = ArrayList<ClrTopLevel>(); val topLevelProps = ArrayList<ClrTopLevelProp>(); var filePkg: FqName? = null; var fileClass = ""   // current [KotlinFile] section
		val projList = ArrayList<Pair<String, String>>()   // (dotNetPrefix, kotlinPrefix) from `nsproj` lines
		fun flush() { if (name.isNotEmpty()) types.add(ClrType(name, dotNet, isObject, isInterface, isAnnotation, isOpen, tparams, supers, ArrayList(methods), ArrayList(ctors), ArrayList(props), ArrayList(events), indexer, iteratorElem, baseNoArgCtor, ArrayList(staticMethods), ArrayList(staticProps), ArrayList(memberExtProps))) }
		for (raw in file.readLines()) {
			val line = raw.trim()
			if (line.isEmpty()) continue
			val tok = line.split(' ')
			when (tok[0]) {
				"package" -> {}   // ignored: types resolve at their real .NET namespace, not a synthetic package
				"object", "class", "interface" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; iteratorElem = null; baseNoArgCtor = true; staticMethods.clear(); staticProps.clear(); memberExtProps.clear(); supers = emptyList(); filePkg = null
					name = tok[1]; dotNet = tok[2]; isObject = tok[0] == "object"; isInterface = tok[0] == "interface"; isAnnotation = false
					isOpen = !isObject && !isInterface && tok.getOrNull(3) == "open"
					// `class <Name> <DotNet> <open|sealed> [<TP>...]` (TPs at 4, after the modality token) vs
					// `interface <Name> <DotNet> [<TP>...]` (TPs at 3, no modality token). `object` has none.
					tparams = when (tok[0]) { "class" -> tok.drop(4); "interface" -> tok.drop(3); else -> emptyList() }
				}
				// annotation <Name> <DotNet> [<param>:<type>]* — a .NET attribute -> Kotlin annotation class; the
				// trailing params (from its longest ctor) become the single annotation constructor.
				"annotation" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; iteratorElem = null; baseNoArgCtor = true; staticMethods.clear(); staticProps.clear(); memberExtProps.clear(); supers = emptyList(); filePkg = null
					name = tok[1]; dotNet = tok[2]; isObject = false; isInterface = false; isAnnotation = true; isOpen = false; tparams = emptyList()
					ctors.add(parseParams(tok.drop(3)))
				}
				// nsproj <kotlinPrefix> <dotNetPrefix> — a Kotlin-package <-> .NET-namespace projection (a referenced
				// assembly declared via [DotKtNamespaceProjection]); applied when deriving Kotlin packages below.
				"nsproj" -> { if (tok.size >= 3) projList.add(tok[2] to tok[1]); projections = projList }
				// file <package> <fileClassFqn> — a Kotlin file facade ([KotlinFile]); subsequent `tlfun` lines are
				// TOP-LEVEL functions in <package> (`-` = root), restored as a .NET static call to <fileClassFqn>.
				"file" -> {
					flush(); name = ""; supers = emptyList()
					// The package may be a .NET namespace that projects to a Kotlin package (e.g. DotKt.Coroutines -> kotlinx.coroutines).
					filePkg = if (tok.getOrNull(1) == "-" || tok.getOrNull(1).isNullOrEmpty()) FqName.ROOT else FqName(toKotlinPkg(tok[1]))
					fileClass = tok.getOrNull(2) ?: ""
				}
				// tlfun <Name> <ret> <prot-?open|final|abstract>[,infix][,operator][,suspend] [<TP>...] [<param>:<type>]*
				"tlfun" -> {
					val fm = parseFunMods(tok.getOrNull(3))
					val m = ClrMethod(tok[1], tok[2], fm.open, fm.abstract, fm.protected,
						parseParams(tok.drop(4)), tok.drop(4).filterNot { it.contains(':') }, fm.infix, fm.operator, fm.suspend, fm.inline, fm.ext)
					topLevel.add(ClrTopLevel(filePkg ?: FqName.ROOT, fileClass, m))
				}
				// tlextprop <Name> <type> <ro|rw> <receiverType> — a top-level EXTENSION property (`val T.p`); the file
				// class holds its get_/set_<Name>(__self) accessors. Restored as a top-level extension property.
				"tlextprop" -> topLevelProps.add(ClrTopLevelProp(filePkg ?: FqName.ROOT, fileClass, tok[1], tok[2], tok.getOrNull(3) == "rw", tok[4]))
				// super <SimpleName>... — the injectable base class (first) + interfaces; wired by ClrSupertypeInjector.
				"super" -> supers = tok.drop(1)
				// basector none — the base class has no accessible no-arg ctor (still linked for assignability, but a
				// synthesized `: super()` delegating call must be suppressed).
				"basector" -> if (tok.getOrNull(1) == "none") baseNoArgCtor = false
				// fun <Name> <ret> <prot-?open|final|abstract> [<TypeParam>...] [<param>:<type>]* — the modifier is a
				// single token (so it never looks like a type param); bare trailing tokens (no `:`) are type params.
				"fun" -> {
					val fm = parseFunMods(tok.getOrNull(3))
					methods.add(ClrMethod(tok[1], tok[2], fm.open, fm.abstract, fm.protected,
						parseParams(tok.drop(4)), tok.drop(4).filterNot { it.contains(':') }, fm.infix, fm.operator, fm.suspend, fm.inline, fm.ext))
				}
				"ctor" -> ctors.add(parseParams(tok.drop(1)))
				// sfun <Name> <ret> [<param>:<type>]* — a public STATIC method of a normal class (-> companion).
				"sfun" -> staticMethods.add(ClrMethod(tok[1], tok[2], false, false, false, parseParams(tok.drop(3))))
				// sprop <Name> <type> <ro|rw> — a public STATIC prop/field of a normal class (-> companion).
				"sprop" -> staticProps.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", false, false, false))
				// prop <Name> <type> <ro|rw> <prot-?open|final|abstract>
				"prop" -> {
					val mod = tok.getOrNull(4) ?: "final"; val prot = mod.startsWith("prot-"); val bare = mod.removePrefix("prot-")
					props.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", bare == "open", bare == "abstract", prot))
				}
				// memextprop <Name> <type> <ro|rw> <receiverType> <prot-?final> — a member extension property `val T.p`
				"memextprop" -> memberExtProps.add(ClrMemberExtProp(tok[1], tok[2], tok.getOrNull(3) == "rw", tok[4], (tok.getOrNull(5) ?: "final").startsWith("prot-")))
				// event <Name> <handlerRet> <handlerParams...>
				"event" -> events.add(ClrEvent(tok[1], tok[2], parseParams(tok.drop(3))))
				// index <indexType> <valueType> <ro|rw> — `this[i]` indexer -> operator get/set.
				"index" -> indexer = ClrIndexer(tok[1], tok[2], tok.getOrNull(3) == "rw")
				// iterator <elem> — IEnumerable<elem> -> a frontend-only `operator fun iterator(): Iterator<elem>`.
				"iterator" -> iteratorElem = tok.getOrNull(1)
			}
		}
		flush()
		val module = ClrModule(types, topLevel, topLevelProps)
		// Record each restored top-level function so the backend emits its call as a .NET static on the file class.
		for (tl in topLevel) {
			val fqn = if (tl.pkg.isRoot) tl.fn.name else "${tl.pkg.asString()}.${tl.fn.name}"
			ClrTopLevelRegistry.register(fqn, tl.fileClassDotNet, tl.fn.suspend)
		}
		// Record each restored top-level extension property so the backend emits its get_/set_ as a .NET static call.
		for (tp in topLevelProps) {
			val fqn = if (tp.pkg.isRoot) tp.name else "${tp.pkg.asString()}.${tp.name}"
			ClrTopLevelRegistry.registerProp(fqn, tp.fileClassDotNet)
		}
		for (t in types) {
			// Register at the real .NET-namespace fqn (e.g. System.Text.StringBuilder), so the backend's clrName
			// resolves the .NET type for `import System.Text.StringBuilder`. Namespace-less types fall back to bare name.
			val ns = namespaceOf(t.dotNetName)   // projected Kotlin package (== .NET namespace when unprojected)
			val fqn = if (ns.isNotEmpty()) "$ns.${t.kotlinName}" else t.kotlinName
			ClrTypeRegistry.register(fqn, t.dotNetName)
			for (e in t.events) {
				ClrEventRegistry.register(fqn, "add_${e.name}", e.name, "+=")
				ClrEventRegistry.register(fqn, "remove_${e.name}", e.name, "-=")
			}
		}
		return module
	}

	private fun parseParams(tokens: List<String>): List<ClrParam> =
		tokens.filter { it.contains(':') }.map { ClrParam(it.substringBefore(':'), it.substringAfter(':')) }

	// Shared lookups (also used by ClrSupertypeInjector). Each .NET type resolves at its REAL namespace, so
	// `import System.Text.StringBuilder` works through Kotlin's normal package machinery (the .NET namespace IS the
	// Kotlin package); nested "+" and generic arity are already stripped in the metadata.
	fun namespaceOf(dotNet: String): String = toKotlinPkg(dotNet.substringBefore('+').substringBeforeLast('.', ""))
	val byClassId: Map<ClassId, ClrType> by lazy {
		module?.types?.associateBy { ClassId(FqName(namespaceOf(it.dotNetName)), Name.identifier(it.kotlinName)) }.orEmpty()
	}
	val classIdByName: Map<String, ClassId> by lazy { byClassId.entries.associate { (id, t) -> t.kotlinName to id } }
	// Generic and non-generic types share a simple name (`IEnumerable<T>` vs `IEnumerable`) — resolve by (name, arity)
	// so `generic:IEnumerable[Item]` picks the generic one and a bare `IEnumerable` picks the non-generic one.
	private val byNameArity: Map<Pair<String, Int>, ClassId> by lazy {
		byClassId.entries.associate { (id, t) -> (t.kotlinName to t.typeParams.size) to id }
	}
	// STRICT on arity, NO fallback. .NET allows a generic and a non-generic type with the same name+namespace
	// (`IComparable` and `IComparable<T>`); Kotlin's ClassId can't tell them apart, so byClassId keeps only one and
	// the other arity is simply absent. Resolving a reference to the ABSENT arity by falling back to the present one
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
 * assemblies — the same reflection that used to emit `.kt`, now feeding the compiler in-memory.
 *
 * Synthesized FIR carries no annotations; [ClrTypeRegistry] (populated at metadata load) tells the
 * backend each type's .NET name. Supported now: `object` (static) + `class` (constructors + instance
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

	// `byref(x)`: a root-package intrinsic marking a call arg as a .NET out/ref parameter. It returns `ClrRef<T>`
	// (the surfaced type of any .NET byref param), so the signature is self-documenting. The backend reads the
	// marker and passes the lvalue's address; `netType(ClrRef<T>)` is `byref:T`.
	private val byrefName = "byref"
	// `ClrRef<T>`: an intrinsic generic type for a managed reference (T&). It is the surfaced type of a .NET out/ref
	// parameter and of a ref-returning method; it is `by`-delegatable (getValue/setValue) so a ref return reads as
	// `var x by m()`. The argument path erases it (the byref(x) marker emits the lvalue's address).
	private val clrRefClassId = ClassId(FqName.ROOT, Name.identifier("ClrRef"))
	// `stackBuffer(n) { buf -> … }` + `StackBuffer<T>`: a scoped stack allocation (CLR `localloc`). The block is
	// splice-inlined so the buffer lives in the caller's frame; `StackBuffer<T>` (size/get/set/asSpan) is erased.
	private val stackBufferName = "stackBuffer"
	private val stackBufferClassId = ClassId(FqName.ROOT, Name.identifier("StackBuffer"))
	// `Span<T>`: a root-package intrinsic that maps to the real `System.Span<T>` (netType/birType -> clrg:System.Span)
	// — the surfaced form of a .NET Span parameter and the result of `StackBuffer.asSpan()`.
	private val spanClassId = ClassId(FqName.ROOT, Name.identifier("Span"))
	// The intrinsics are CLR-context features -> available whenever .NET interop is active (metadata loaded).
	private val clrActive = module != null

	override fun hasPackage(packageFqName: FqName): Boolean =
		clrActive && (packageFqName in packages || packageFqName in topLevelPackages || packageFqName.isRoot)

	override fun getTopLevelCallableIds(): Set<CallableId> =
		if (!clrActive) emptySet()
		else hashSetOf(CallableId(FqName.ROOT, Name.identifier(byrefName)), CallableId(FqName.ROOT, Name.identifier(stackBufferName))) + topLevelByCallable.keys + topLevelPropByCallable.keys

	override fun getTopLevelClassIds(): Set<ClassId> =
		if (!clrActive) byClassId.keys else byClassId.keys + clrRefClassId + stackBufferClassId + spanClassId

	override fun generateTopLevelClassLikeDeclaration(classId: ClassId): FirClassLikeSymbol<*>? {
		// The intrinsic `ClrRef<T>` carries getValue/setValue (so a ref return is `by`-delegatable).
		if (classId == clrRefClassId || classId == stackBufferClassId || classId == spanClassId) return createTopLevelClass(classId, ClrGeneratedKey, ClassKind.CLASS) {
			typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
		}.symbol
		val type = byClassId[classId] ?: return null
		val kind = when { type.isAnnotation -> ClassKind.ANNOTATION_CLASS; type.isObject -> ClassKind.OBJECT; type.isInterface -> ClassKind.INTERFACE; else -> ClassKind.CLASS }
		// A non-sealed .NET class is `open` so Kotlin can inherit it (the basis of framework-direct UI).
		return createTopLevelClass(classId, ClrGeneratedKey, kind) {
			if (type.open || type.isInterface) modality = Modality.OPEN
			// Generic .NET type (`Collection<T>`) -> declare its type parameters (invariant; bounds omitted).
			for (tp in type.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
			// Supertypes: a class's base (`Button` -> `Widget`, for assignability + inherited/protected members),
			// and an interface's GENERIC base interfaces (`IList<T>` -> `ICollection<T>`, so inherited members like
			// `Add` surface — item 3). A spec is either a simple name or `generic:Open[arg,arg]` (args are the owner's
			// type params, resolved against `tps` below). Deferred provider form -> lazy cross-generation.
			for (spec in type.superTypes) {
				val (openName, arity) = if (spec.startsWith("generic:")) {
					val rest = spec.removePrefix("generic:")
					val br = rest.indexOf('[')
					if (br < 0) rest to 0
					else rest.substring(0, br) to splitTopLevel(rest.substring(br + 1, rest.length - 1)).size
				} else spec to 0
				val scid = ClrMetadataHolder.classIdFor(openName, arity) ?: continue
				superType { tps -> superTypeCone(spec, scid, tps) }
			}
		}.symbol
	}

	/** The owner ClrType of a companion symbol (its static members live here), or null if not a companion-with-statics. */
	private fun companionOwnerType(classId: ClassId): ClrType? =
		if (classId.shortClassName == SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT)
			classId.outerClassId?.let { byClassId[it] }?.takeIf { it.staticMethods.isNotEmpty() || it.staticProps.isNotEmpty() }
		else null

	// A normal class with public STATIC members gets a synthesized companion object holding them, so `App.Start(..)`/
	// `App.Current` resolve (Kotlin has no bare statics). The backend emits .NET static calls for these.
	override fun getNestedClassifiersNames(classSymbol: FirClassSymbol<*>, context: NestedClassGenerationContext): Set<Name> {
		val type = byClassId[classSymbol.classId] ?: return emptySet()
		return if (type.staticMethods.isNotEmpty() || type.staticProps.isNotEmpty())
			setOf(SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT) else emptySet()
	}

	override fun generateNestedClassLikeDeclaration(owner: FirClassSymbol<*>, name: Name, context: NestedClassGenerationContext): FirClassLikeSymbol<*>? {
		if (name != SpecialNames.DEFAULT_NAME_FOR_COMPANION_OBJECT) return null
		val type = byClassId[owner.classId] ?: return null
		if (type.staticMethods.isEmpty() && type.staticProps.isEmpty()) return null
		return createCompanionObject(owner, ClrGeneratedKey).symbol
	}

	override fun getCallableNamesForClass(classSymbol: FirClassSymbol<*>, context: MemberGenerationContext): Set<Name> {
		// `ClrRef<T>` exposes getValue/setValue so a ref return is `by`-delegatable (`var x by byref(m())`).
		if (classSymbol.classId == clrRefClassId) return hashSetOf(Name.identifier("getValue"), Name.identifier("setValue"))
		// `StackBuffer<T>`: size (val), get/set (operators), asSpan (-> Span<T> = the real System.Span<T>).
		if (classSymbol.classId == stackBufferClassId)
			return hashSetOf(Name.identifier("size"), Name.identifier("get"), Name.identifier("set"), Name.identifier("asSpan"))
		// A companion object: its callables are the owner class's static methods/props.
		companionOwnerType(classSymbol.classId)?.let { ct ->
			val n = HashSet<Name>()
			ct.staticMethods.forEach { n.add(Name.identifier(it.name)) }
			ct.staticProps.forEach { n.add(Name.identifier(it.name)) }
			return n
		}
		val type = byClassId[classSymbol.classId] ?: return emptySet()
		val names = type.methods.mapTo(HashSet()) { Name.identifier(it.name) }
		type.properties.forEach { names.add(Name.identifier(it.name)) }
		type.memberExtProps.forEach { names.add(Name.identifier(it.name)) }
		type.events.forEach { names.add(Name.identifier("add_${it.name}")); names.add(Name.identifier("remove_${it.name}")) }
		type.indexer?.let { names.add(Name.identifier("get")); if (it.mutable) names.add(Name.identifier("set")) }
		if (type.iteratorElem != null) names.add(Name.identifier("iterator"))
		if (!type.isObject && !type.isInterface) names.add(SpecialNames.INIT)   // signals generateConstructors
		return names
	}

	override fun generateProperties(callableId: CallableId, context: MemberGenerationContext?): List<FirPropertySymbol> {
		// DotKt round-trip: a top-level EXTENSION property (`val T.p`) — no owner; the backend routes `x.p` to the
		// file class's get_/set_<p>(__self) statics. No backing field (the accessors carry the value).
		topLevelPropByCallable[callableId]?.let { tp ->
			return listOf(createTopLevelProperty(ClrGeneratedKey, callableId, coneOf(tp.type, null), !tp.mutable, false) {
				extensionReceiverType(coneOf(tp.receiver, null))
			}.symbol)
		}
		val owner = context?.owner ?: return emptyList()
		// `StackBuffer<T>.size: Int` (the element count).
		if (owner.classId == stackBufferClassId && callableId.callableName.asString() == "size")
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.intType.coneType, true, false).symbol)
		// A companion object holds the owner class's STATIC props/fields (App.Current). Backend emits .NET static get.
		companionOwnerType(owner.classId)?.let { ct ->
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
			// lowered (construction is native clrNew) so the missing delegation is harmless.
			createConstructor(context.owner, ClrGeneratedKey, i == 0, type.baseNoArgCtor) {
				for (p in params) valueParameter(Name.identifier(p.name), coneOf(p.type, context.owner))
			}.symbol
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
			// so a top-level fun carries at most `suspend`; the backend (ClrTopLevelRegistry) emits the static call.
			topLevelByCallable[callableId]?.let { tls ->
				return tls.flatMap { tl ->
					val m = tl.fn
					// An extension fun: the first param `__self` is the receiver (rest are value params).
					val extRecv = if (m.ext && m.params.isNotEmpty()) m.params[0] else null
					val vps = if (extRecv != null) m.params.drop(1) else m.params
					// Default args have no .NET analog that fir2ir can lower (a plugin `hasDefaultValue` inserts a STUB
					// expression that crashes fir2ir). Restore them @JvmOverloads-style: one overload per trailing
					// contiguous default param omitted; the consumer resolves by arity and ilemit fills the rest from
					// [DefaultParameterValue]. coneOfMethod strips the `opt:` marker, so each overload's params are required.
					val trailingOpt = vps.reversed().takeWhile { it.type.startsWith("opt:") }.count()
					// One path for both ordinary and GENERIC top-level functions: resolve every spec against the fn's own
					// type params (empty for a non-generic fn -> coneOfMethod falls through to coneOf), so a generic fn
					// keeps its extension receiver / inline / infix / operator / vararg / default-arg overloads too.
					((vps.size - trailingOpt)..vps.size).map { arity ->
						createTopLevelFunction(ClrGeneratedKey, callableId, { tps -> coneOfMethod(m.returnType, null, m.typeParams, tps) }) {
							for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
							if (m.suspend) status { isSuspend = true }
							if (m.inline) status { isInline = true }   // accept non-local return; ilemit splices the carried body
							if (m.infix || m.operator) status { isInfix = m.infix; isOperator = m.operator }   // top-level extension operators
							if (extRecv != null) extensionReceiverType { tps -> coneOfMethod(extRecv.type, null, m.typeParams, tps) }
							for (p in vps.take(arity))
								if (p.type.startsWith("vararg:")) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod("array:" + p.type.removePrefix("vararg:"), null, m.typeParams, tps) }, isVararg = true)
								else valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, null, m.typeParams, tps) })
						}.symbol
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
		// A companion object holds the owner class's STATIC methods (App.Start(..)). The backend emits .NET static calls.
		companionOwnerType(owner.classId)?.let { ct ->
			val cn = callableId.callableName.asString()
			return ct.staticMethods.filter { it.name == cn }.map { m ->
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, coneOf(m.returnType, owner)) {
					for (p in m.params) valueParameter(Name.identifier(p.name), coneOf(p.type, owner))
				}.symbol
			}
		}
		val type = byClassId[owner.classId] ?: return emptyList()
		val callName = callableId.callableName.asString()

		// Event subscribe/unsubscribe: `add_<E>`/`remove_<E>` take a handler lambda; the backend
		// rewrites the call to `receiver.<E> += handler` / `-= handler` (see ClrEventRegistry).
		val event = type.events.firstOrNull { "add_${it.name}" == callName || "remove_${it.name}" == callName }
		if (event != null) {
			val handler = coneFunctionType(event.handlerParams.map { coneOf(it.type, owner) }, coneOf(event.handlerReturn, owner))
			val fn = createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.unitType.coneType) {
				valueParameter(Name.identifier("handler"), handler)
			}
			return listOf(fn.symbol)
		}

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
			val ret = iterSym?.constructType(arrayOf(coneOf(type.iteratorElem, owner)), false)
				?: session.builtinTypes.nullableAnyType.coneType
			val fn = createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, ret) {
				status { isOperator = true }
				if (type.open && !type.isObject) modality = Modality.OPEN
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
				// Default args: @JvmOverloads-style — one overload per trailing contiguous default param omitted (a plugin
				// `hasDefaultValue` would insert a fir2ir-crashing STUB). The consumer resolves by arity; ilemit fills the
				// omitted args from [DefaultParameterValue]. trailingOpt==0 (the common case) => exactly one function.
				val trailingOpt = vps.reversed().takeWhile { it.type.startsWith("opt:") }.count()
				((vps.size - trailingOpt)..vps.size).map { arity ->
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, coneOf(m.returnType, owner)) {
					// interface members + .NET abstract => ABSTRACT (must implement); .NET virtual => OPEN (overridable).
					if (type.isInterface || m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected   // overridable protected lifecycle methods (item 2)
					// DotKt round-trip: Kotlin modifiers with no .NET analog, restored from [KotlinFunction].
					if (m.infix || m.operator || m.suspend) status { isInfix = m.infix; isOperator = m.operator; isSuspend = m.suspend }
					if (m.inline) status { isInline = true }
					if (extRecv != null) extensionReceiverType(coneOf(extRecv.type, owner))
					for (p in vps.take(arity))
						if (p.type.startsWith("vararg:")) valueParameter(Name.identifier(p.name), coneOf("array:" + p.type.removePrefix("vararg:"), owner), isVararg = true)
						else valueParameter(Name.identifier(p.name), coneOf(p.type, owner))
				}.symbol
				}
			} else listOf(
				// A generic .NET method (`SizeOf<T>()`, `As<T>(o): T`). Declare its method type parameters, then
				// resolve the return type and any T-typed param/receiver against THOSE params (via the provider forms,
				// since the type params don't exist until the function is being built). The CLR has reified generics,
				// so the backend just emits a generic .NET method call (MakeGenericMethod) — see [[clr-not-jvm-discard-jvmisms]].
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
					{ tps -> coneOfMethod(m.returnType, owner, m.typeParams, tps) }) {
					if (m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected
					if (m.infix || m.operator || m.suspend) status { isInfix = m.infix; isOperator = m.operator; isSuspend = m.suspend }
					if (m.inline) status { isInline = true }
					for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					if (extRecv != null) extensionReceiverType { tps -> coneOfMethod(extRecv.type, owner, m.typeParams, tps) }
					for (p in vps) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, owner, m.typeParams, tps) })
				}.symbol
			)
		}
	}

	/** Resolve a supertype spec (a simple injected name, or `generic:Open[arg,arg]`) to a ConeKotlinType, mapping
	 *  type-argument names to the owner's own type parameters (`tps`, available in the class-builder superType form). */
	private fun superTypeCone(spec: String, scid: ClassId, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(scid) ?: return session.builtinTypes.anyType.coneType
		if (!spec.startsWith("generic:")) return sym.constructType(emptyArray(), false)
		val rest = spec.removePrefix("generic:"); val br = rest.indexOf('[')
		val argStr = if (br < 0) "" else rest.substring(br + 1, rest.length - 1)
		val args = if (argStr.isEmpty()) emptyList() else splitTopLevel(argStr).map { superArgCone(it, tps) }
		@Suppress("UNCHECKED_CAST")
		return sym.constructType(args.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>, false)
	}

	/** A supertype type-argument: the owner's type parameter (matched by name in [tps]), else a primitive or another
	 *  injected type. (Interface supertype args are almost always the owner's own type params, e.g. `ICollection<T>`.) */
	private fun superArgCone(name: String, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		tps.firstOrNull { it.symbol.name.identifier == name }?.let { return it.symbol.constructType(emptyArray(), false) }
		val bt = session.builtinTypes
		return when (name) {
			"Int" -> bt.intType.coneType; "Long" -> bt.longType.coneType; "Double" -> bt.doubleType.coneType
			"Float" -> bt.floatType.coneType; "Short" -> bt.shortType.coneType; "Byte" -> bt.byteType.coneType
			"Boolean" -> bt.booleanType.coneType; "Char" -> bt.charType.coneType; "String" -> bt.stringType.coneType
			// Build the cross-type arg from its ClassId LOOKUP TAG, not by resolving the symbol. A self-referential
			// supertype (`Money : IComparable<Money>`) runs THIS lambda synchronously while `Money` is still being
			// built (not yet cached), so resolving its symbol here re-enters generation -> StackOverflow. A lookup-tag
			// cone is a lazy by-ClassId reference; the symbol resolves later, once `Money` is fully built.
			else -> {
				// A fully-qualified arg (`P.Money`, from the FQN member/arg encoding) resolves to that exact ClassId;
				// a bare name resolves by (simpleName, arity 0). Both via a lazy lookup-tag cone (self-ref safe).
				val cid = if ('.' in name) ClassId(FqName(name.substringBeforeLast('.')), Name.identifier(name.substringAfterLast('.')))
					else ClrMetadataHolder.classIdFor(name, 0)
				cid?.let { ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(it), emptyArray(), false) } ?: bt.nullableAnyType.coneType
			}
		}
	}

	/** Like [coneOf], but also resolves a reference to one of the method's own generic parameters (`T`). owner is
	 *  null for a top-level function (no enclosing class type params). */
	private fun coneOfMethod(typeName: String, owner: FirClassSymbol<*>?, methodTypeParams: List<String>,
	                         tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType =
		// Resolve the spec through `coneOf`, but with a type-variable resolver so a method type param `T` — even nested
		// inside a `generic:Box[T]` arg — binds to the function's own type parameter (not the owner's, and not Any?).
		coneOf(typeName, owner) { name ->
			val i = methodTypeParams.indexOf(name)
			if (i >= 0 && i < tps.size) tps[i].symbol.constructType(emptyArray(), false) else null
		}

	/** A Kotlin function type `(P...) -> R` = `kotlin.FunctionN<P..., R>`, for event handler params. */
	private fun coneFunctionType(params: List<ConeKotlinType>, ret: ConeKotlinType): ConeKotlinType {
		val cid = ClassId(FqName("kotlin"), Name.identifier("Function${params.size}"))
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return session.builtinTypes.nullableAnyType.coneType
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

	/** Split a metadata type list (`generic:Box[V]` / `func:[ret,a,b]` children) on commas at bracket-depth 0, so a
	 *  compound child keeps its own `[...]` intact. Mirrors ilemit's SplitTopLevel — the recursive grammar's parser. */
	private fun splitTopLevel(s: String): List<String> {
		if (s.isEmpty()) return emptyList()
		val res = ArrayList<String>(); var depth = 0; var start = 0
		for (i in s.indices) when (s[i]) {
			'[' -> depth++
			']' -> depth--
			',' -> if (depth == 0) { res.add(s.substring(start, i)); start = i + 1 }
		}
		res.add(s.substring(start))
		return res
	}

	// `tv` resolves a bare type-variable name (a method/function type parameter) to its cone type; null when the name
	// isn't one. Threaded through every recursion so a `T` nested in `generic:Box[T]`/`array:T`/`func:…` also binds.
	private fun coneOf(typeName: String, owner: FirClassSymbol<*>?, tv: ((String) -> ConeKotlinType?)? = null): ConeKotlinType {
		// A trailing `?` -> the Kotlin nullable form `T?` (so a consumer can pass/handle null). Carried by [KotlinNullable].
		if (typeName.endsWith("?")) return coneOf(typeName.dropLast(1), owner, tv).withNullability(true, session.typeContext)
		// `opt:T` marks a default-arg param (the optionality is set via hasDefaultValue at the param; the type is T).
		if (typeName.startsWith("opt:")) return coneOf(typeName.removePrefix("opt:"), owner, tv)
		// A .NET out/ref param / ref return (`byref:Int`) -> the intrinsic `ClrRef<Int>`.
		if (typeName.startsWith("byref:")) return clrRefOf(coneOf(typeName.removePrefix("byref:"), owner, tv))
		// A .NET `Span<T>` param (`span:Int`) -> the intrinsic `Span<Int>` (the real System.Span<Int>).
		if (typeName.startsWith("span:")) return spanOf(coneOf(typeName.removePrefix("span:"), owner, tv))
		// (4) A .NET delegate parameter (`func:<ret>:<arg>,<arg>`) -> a Kotlin function type `(args) -> ret`, so a
		// lambda binds and overloads disambiguate. The backend builds the real delegate from the call-site param type.
		if (typeName.startsWith("func:")) {
			// `func:[ret,arg,arg]` — bracketed, split at bracket-depth 0 so a compound child (`generic:Box[V]`) nests.
			val parts = splitTopLevel(typeName.removePrefix("func:").removeSurrounding("[", "]"))
			val ret = parts.firstOrNull() ?: "Unit"; val args = parts.drop(1)
			return coneFunctionType(args.map { coneOf(it, owner, tv) }, coneOf(ret, owner, tv))
		}
		// (3) A constructed generic (`generic:IList[ResourceDictionary]`, or `generic:Box[T]` of a generic fn) -> the
		// injected open type applied to the (recursively resolved) args, so chained members / type inference work.
		if (typeName.startsWith("generic:")) {
			val rest = typeName.removePrefix("generic:")
			val br = rest.indexOf('[')
			val open = if (br < 0) rest else rest.substring(0, br)
			val inner = if (br < 0) "" else rest.substring(br + 1, rest.length - 1)
			val argNames = if (inner.isEmpty()) emptyList() else splitTopLevel(inner)
			val cid = ClrMetadataHolder.classIdFor(open, argNames.size) ?: return session.builtinTypes.nullableAnyType.coneType
			val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return session.builtinTypes.nullableAnyType.coneType
			val args = argNames.map { coneOf(it, owner, tv) }
			@Suppress("UNCHECKED_CAST")
			return sym.constructType(args.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>, false)
		}
		// A bare type-variable name (`T`): a function type parameter (via `tv`) takes priority, then the owner's own.
		tv?.invoke(typeName)?.let { return it }
		owner?.typeParameterSymbols?.firstOrNull { it.name.identifier == typeName }
			?.let { return it.constructType(emptyArray(), false) }
		val bt = session.builtinTypes
		// A .NET array param/return (`array:String` -> Kotlin `Array<String>` / primitive `IntArray`).
		if (typeName.startsWith("array:")) {
			val elem = coneOf(typeName.removePrefix("array:"), owner, tv)
			val prim = mapOf("Int" to "IntArray", "Long" to "LongArray", "Double" to "DoubleArray", "Float" to "FloatArray",
				"Short" to "ShortArray", "Byte" to "ByteArray", "Boolean" to "BooleanArray", "Char" to "CharArray")[typeName.removePrefix("array:")]
			val cid = if (prim != null) ClassId(FqName("kotlin"), Name.identifier(prim))
				else ClassId(FqName("kotlin"), Name.identifier("Array"))
			val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return bt.nullableAnyType.coneType
			return sym.constructType(if (prim != null) emptyArray() else arrayOf(elem), false)
		}
		return when (typeName) {
			"Int" -> bt.intType.coneType
			"Long" -> bt.longType.coneType
			"Double" -> bt.doubleType.coneType
			"Float" -> bt.floatType.coneType
			"Short" -> bt.shortType.coneType
			"Byte" -> bt.byteType.coneType
			"Boolean" -> bt.booleanType.coneType
			"Char" -> bt.charType.coneType
			"String" -> bt.stringType.coneType
			"Unit" -> bt.unitType.coneType
			else -> {
				// A fully-qualified cross-type (`Microsoft.UI.Xaml.LaunchActivatedEventArgs`) resolves to that EXACT
				// ClassId — disambiguating same-simple-name types from different namespaces. A bare name (legacy /
				// nested fallback) resolves by (simpleName, arity 0).
				val cid = if ('.' in typeName)
					ClassId(FqName(typeName.substringBeforeLast('.')), Name.identifier(typeName.substringAfterLast('.')))
				else ClrMetadataHolder.classIdFor(typeName, 0)
				val sym = when {
					cid == null -> null
					cid == owner?.classId -> owner
					else -> session.symbolProvider.getClassLikeSymbolByClassId(cid)
				}
				sym?.constructType(emptyArray(), false) ?: bt.nullableAnyType.coneType
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
 * registrar. Wired into [clrc.pipeline.ClrCliPipeline] via `COMPILER_PLUGIN_REGISTRARS`.
 */
class ClrCompilerPluginRegistrar : CompilerPluginRegistrar() {
	override val supportsK2: Boolean = true
	override fun ExtensionStorage.registerExtensions(configuration: CompilerConfiguration) {
		FirExtensionRegistrarAdapter.registerExtension(ClrFirExtensionRegistrar())
	}
}
