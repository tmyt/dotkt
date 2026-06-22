@file:OptIn(
	org.jetbrains.kotlin.fir.extensions.FirExtensionApiInternals::class,
	org.jetbrains.kotlin.fir.extensions.ExperimentalTopLevelDeclarationsGenerationApi::class,
	org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
)

package clrc.frontend

import clrc.ClrEventRegistry
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
import org.jetbrains.kotlin.fir.plugin.createConstructor
import org.jetbrains.kotlin.fir.plugin.createMemberFunction
import org.jetbrains.kotlin.fir.plugin.createMemberProperty
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
private class ClrMethod(val name: String, val returnType: String, val open: Boolean, val abstract: Boolean, val protected: Boolean, val params: List<ClrParam>, val typeParams: List<String> = emptyList())
private class ClrProperty(val name: String, val type: String, val mutable: Boolean, val open: Boolean, val abstract: Boolean, val protected: Boolean)
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
)
private class ClrModule(val types: List<ClrType>)

/**
 * Loads the .NET type metadata to inject, once per process. The path comes from `CLR_TYPES_METADATA`
 * (set by the build / MSBuild / verify harness). Absent or empty => inject nothing, so compilations
 * that don't opt in are completely unaffected. Each injected type is also recorded in [ClrTypeRegistry]
 * so the backend maps it to its .NET name.
 */
private object ClrMetadataHolder {
	val module: ClrModule? by lazy { System.getenv("CLR_TYPES_METADATA")?.let { load(File(it)) } }

	private fun load(file: File): ClrModule? {
		if (!file.isFile) return null
		val types = ArrayList<ClrType>()
		var name = ""; var dotNet = ""; var isObject = false; var isInterface = false; var isOpen = false; var isAnnotation = false
		var tparams = emptyList<String>(); var supers = emptyList<String>()
		val methods = ArrayList<ClrMethod>(); val ctors = ArrayList<List<ClrParam>>()
		val props = ArrayList<ClrProperty>(); val events = ArrayList<ClrEvent>()
		var indexer: ClrIndexer? = null
		fun flush() { if (name.isNotEmpty()) types.add(ClrType(name, dotNet, isObject, isInterface, isAnnotation, isOpen, tparams, supers, ArrayList(methods), ArrayList(ctors), ArrayList(props), ArrayList(events), indexer)) }
		for (raw in file.readLines()) {
			val line = raw.trim()
			if (line.isEmpty()) continue
			val tok = line.split(' ')
			when (tok[0]) {
				"package" -> {}   // ignored: types resolve at their real .NET namespace, not a synthetic package
				"object", "class", "interface" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; supers = emptyList()
					name = tok[1]; dotNet = tok[2]; isObject = tok[0] == "object"; isInterface = tok[0] == "interface"; isAnnotation = false
					isOpen = !isObject && !isInterface && tok.getOrNull(3) == "open"
					// `class <Name> <DotNet> <open|sealed> [<TP>...]` (TPs at 4, after the modality token) vs
					// `interface <Name> <DotNet> [<TP>...]` (TPs at 3, no modality token). `object` has none.
					tparams = when (tok[0]) { "class" -> tok.drop(4); "interface" -> tok.drop(3); else -> emptyList() }
				}
				// annotation <Name> <DotNet> [<param>:<type>]* — a .NET attribute -> Kotlin annotation class; the
				// trailing params (from its longest ctor) become the single annotation constructor.
				"annotation" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; supers = emptyList()
					name = tok[1]; dotNet = tok[2]; isObject = false; isInterface = false; isAnnotation = true; isOpen = false; tparams = emptyList()
					ctors.add(parseParams(tok.drop(3)))
				}
				// super <SimpleName>... — the injectable base class (first) + interfaces; wired by ClrSupertypeInjector.
				"super" -> supers = tok.drop(1)
				// fun <Name> <ret> <prot-?open|final|abstract> [<TypeParam>...] [<param>:<type>]* — the modifier is a
				// single token (so it never looks like a type param); bare trailing tokens (no `:`) are type params.
				"fun" -> {
					val mod = tok.getOrNull(3) ?: "final"; val prot = mod.startsWith("prot-"); val bare = mod.removePrefix("prot-")
					methods.add(ClrMethod(tok[1], tok[2], bare == "open", bare == "abstract", prot,
						parseParams(tok.drop(4)), tok.drop(4).filterNot { it.contains(':') }))
				}
				"ctor" -> ctors.add(parseParams(tok.drop(1)))
				// prop <Name> <type> <ro|rw> <prot-?open|final|abstract>
				"prop" -> {
					val mod = tok.getOrNull(4) ?: "final"; val prot = mod.startsWith("prot-"); val bare = mod.removePrefix("prot-")
					props.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", bare == "open", bare == "abstract", prot))
				}
				// event <Name> <handlerRet> <handlerParams...>
				"event" -> events.add(ClrEvent(tok[1], tok[2], parseParams(tok.drop(3))))
				// index <indexType> <valueType> <ro|rw> — `this[i]` indexer -> operator get/set.
				"index" -> indexer = ClrIndexer(tok[1], tok[2], tok.getOrNull(3) == "rw")
			}
		}
		flush()
		val module = ClrModule(types)
		for (t in types) {
			// Register at the real .NET-namespace fqn (e.g. System.Text.StringBuilder), so the backend's clrName
			// resolves the .NET type for `import System.Text.StringBuilder`. Namespace-less types fall back to bare name.
			val ns = t.dotNetName.substringBefore('+').substringBeforeLast('.', "")
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
	fun namespaceOf(dotNet: String): String = dotNet.substringBefore('+').substringBeforeLast('.', "")
	val byClassId: Map<ClassId, ClrType> by lazy {
		module?.types?.associateBy { ClassId(FqName(namespaceOf(it.dotNetName)), Name.identifier(it.kotlinName)) }.orEmpty()
	}
	val classIdByName: Map<String, ClassId> by lazy { byClassId.entries.associate { (id, t) -> t.kotlinName to id } }
	// Generic and non-generic types share a simple name (`IEnumerable<T>` vs `IEnumerable`) — resolve by (name, arity)
	// so `generic:IEnumerable:Item` picks the generic one and a bare `IEnumerable` picks the non-generic one.
	private val byNameArity: Map<Pair<String, Int>, ClassId> by lazy {
		byClassId.entries.associate { (id, t) -> (t.kotlinName to t.typeParams.size) to id }
	}
	// STRICT on arity: a generic supertype/type must resolve to a type with the MATCHING number of type parameters.
	// Falling back to a different-arity type (e.g. resolving `generic:IEnumerable:Item` to non-generic IEnumerable)
	// builds a generic type with the wrong number of arguments and crashes the fir2ir fake-override builder
	// ("typeParameters size != typeArguments size"). Only arity 0 falls back to the simple-name map (always non-generic).
	fun classIdFor(name: String, arity: Int): ClassId? = byNameArity[name to arity] ?: if (arity == 0) classIdByName[name] else null
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
		clrActive && (packageFqName in packages || packageFqName.isRoot)

	override fun getTopLevelCallableIds(): Set<CallableId> =
		if (!clrActive) emptySet() else hashSetOf(CallableId(FqName.ROOT, Name.identifier(byrefName)), CallableId(FqName.ROOT, Name.identifier(stackBufferName)))

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
			// `Add` surface — item 3). A spec is either a simple name or `generic:Open:arg,arg` (args are the owner's
			// type params, resolved against `tps` below). Deferred provider form -> lazy cross-generation.
			for (spec in type.superTypes) {
				val (openName, arity) = if (spec.startsWith("generic:")) {
					val rest = spec.removePrefix("generic:")
					rest.substringBefore(':') to rest.substringAfter(':', "").let { if (it.isEmpty()) 0 else it.split(',').size }
				} else spec to 0
				val scid = ClrMetadataHolder.classIdFor(openName, arity) ?: continue
				superType { tps -> superTypeCone(spec, scid, tps) }
			}
		}.symbol
	}

	override fun getCallableNamesForClass(classSymbol: FirClassSymbol<*>, context: MemberGenerationContext): Set<Name> {
		// `ClrRef<T>` exposes getValue/setValue so a ref return is `by`-delegatable (`var x by byref(m())`).
		if (classSymbol.classId == clrRefClassId) return hashSetOf(Name.identifier("getValue"), Name.identifier("setValue"))
		// `StackBuffer<T>`: size (val), get/set (operators), asSpan (-> Span<T> = the real System.Span<T>).
		if (classSymbol.classId == stackBufferClassId)
			return hashSetOf(Name.identifier("size"), Name.identifier("get"), Name.identifier("set"), Name.identifier("asSpan"))
		val type = byClassId[classSymbol.classId] ?: return emptySet()
		val names = type.methods.mapTo(HashSet()) { Name.identifier(it.name) }
		type.properties.forEach { names.add(Name.identifier(it.name)) }
		type.events.forEach { names.add(Name.identifier("add_${it.name}")); names.add(Name.identifier("remove_${it.name}")) }
		type.indexer?.let { names.add(Name.identifier("get")); if (it.mutable) names.add(Name.identifier("set")) }
		if (!type.isObject && !type.isInterface) names.add(SpecialNames.INIT)   // signals generateConstructors
		return names
	}

	override fun generateProperties(callableId: CallableId, context: MemberGenerationContext?): List<FirPropertySymbol> {
		val owner = context?.owner ?: return emptyList()
		// `StackBuffer<T>.size: Int` (the element count).
		if (owner.classId == stackBufferClassId && callableId.callableName.asString() == "size")
			return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, session.builtinTypes.intType.coneType, true, false).symbol)
		val type = byClassId[owner.classId] ?: return emptyList()
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
			createConstructor(context.owner, ClrGeneratedKey, i == 0, true) {
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

		val overloads = type.methods.filter { it.name == callName }
		// Member name == .NET name verbatim, so the backend emits the call as-is (no per-member map).
		return overloads.map { m ->
			if (m.typeParams.isEmpty()) {
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName, coneOf(m.returnType, owner)) {
					// interface members + .NET abstract => ABSTRACT (must implement); .NET virtual => OPEN (overridable).
					if (type.isInterface || m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected   // overridable protected lifecycle methods (item 2)
					for (p in m.params) valueParameter(Name.identifier(p.name), coneOf(p.type, owner))
				}.symbol
			} else {
				// A generic .NET method (`SizeOf<T>()`, `As<T>(o): T`). Declare its method type parameters, then
				// resolve the return type and any T-typed value params against THOSE params (via the provider forms,
				// since the type params don't exist until the function is being built). The CLR has reified generics,
				// so the backend just emits a generic .NET method call (MakeGenericMethod) — see [[clr-not-jvm-discard-jvmisms]].
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
					{ tps -> coneOfMethod(m.returnType, owner, m.typeParams, tps) }) {
					if (m.abstract) modality = Modality.ABSTRACT
					else if (m.open && !type.isObject) modality = Modality.OPEN
					if (m.protected) visibility = Visibilities.Protected
					for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					for (p in m.params) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, owner, m.typeParams, tps) })
				}.symbol
			}
		}
	}

	/** Resolve a supertype spec (a simple injected name, or `generic:Open:arg,arg`) to a ConeKotlinType, mapping
	 *  type-argument names to the owner's own type parameters (`tps`, available in the class-builder superType form). */
	private fun superTypeCone(spec: String, scid: ClassId, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		val sym = session.symbolProvider.getClassLikeSymbolByClassId(scid) ?: return session.builtinTypes.anyType.coneType
		if (!spec.startsWith("generic:")) return sym.constructType(emptyArray(), false)
		val argStr = spec.removePrefix("generic:").substringAfter(':', "")
		val args = if (argStr.isEmpty()) emptyList() else argStr.split(',').map { superArgCone(it, tps) }
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
			else -> ClrMetadataHolder.classIdFor(name, 0)?.let { session.symbolProvider.getClassLikeSymbolByClassId(it)?.constructType(emptyArray(), false) } ?: bt.nullableAnyType.coneType
		}
	}

	/** Like [coneOf], but also resolves a reference to one of the method's own generic parameters (`T`). */
	private fun coneOfMethod(typeName: String, owner: FirClassSymbol<*>, methodTypeParams: List<String>,
	                         tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType {
		val i = methodTypeParams.indexOf(typeName)
		if (i >= 0 && i < tps.size) return tps[i].symbol.constructType(emptyArray(), false)
		return coneOf(typeName, owner)
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

	private fun coneOf(typeName: String, owner: FirClassSymbol<*>): ConeKotlinType {
		// A .NET out/ref param / ref return (`byref:Int`) -> the intrinsic `ClrRef<Int>`.
		if (typeName.startsWith("byref:")) return clrRefOf(coneOf(typeName.removePrefix("byref:"), owner))
		// A .NET `Span<T>` param (`span:Int`) -> the intrinsic `Span<Int>` (the real System.Span<Int>).
		if (typeName.startsWith("span:")) return spanOf(coneOf(typeName.removePrefix("span:"), owner))
		// (4) A .NET delegate parameter (`func:<ret>:<arg>,<arg>`) -> a Kotlin function type `(args) -> ret`, so a
		// lambda binds and overloads disambiguate. The backend builds the real delegate from the call-site param type.
		if (typeName.startsWith("func:")) {
			val rest = typeName.removePrefix("func:")
			val ret = rest.substringBefore(':'); val argStr = rest.substringAfter(':', "")
			val args = if (argStr.isEmpty()) emptyList() else argStr.split(',').map { coneOf(it, owner) }
			return coneFunctionType(args, coneOf(ret, owner))
		}
		// (3) A constructed generic (`generic:IList:ResourceDictionary`) -> the injected open type applied to the
		// (recursively resolved) args, so `x.MergedDictionaries.Add(..)` / `for (d in coll)` reach real members.
		if (typeName.startsWith("generic:")) {
			val rest = typeName.removePrefix("generic:")
			val open = rest.substringBefore(':'); val argStr = rest.substringAfter(':', "")
			val argNames = if (argStr.isEmpty()) emptyList() else argStr.split(',')
			val cid = ClrMetadataHolder.classIdFor(open, argNames.size) ?: return session.builtinTypes.nullableAnyType.coneType
			val sym = session.symbolProvider.getClassLikeSymbolByClassId(cid) ?: return session.builtinTypes.nullableAnyType.coneType
			val args = argNames.map { coneOf(it, owner) }
			@Suppress("UNCHECKED_CAST")
			return sym.constructType(args.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>, false)
		}
		// A reference to the owner's own generic type parameter (`T` in `Collection<T>.Add(item: T)`).
		owner.typeParameterSymbols.firstOrNull { it.name.identifier == typeName }
			?.let { return it.constructType(emptyArray(), false) }
		val bt = session.builtinTypes
		// A .NET array param/return (`array:String` -> Kotlin `Array<String>` / primitive `IntArray`).
		if (typeName.startsWith("array:")) {
			val elem = coneOf(typeName.removePrefix("array:"), owner)
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
				val cid = ClrMetadataHolder.classIdFor(typeName, 0)   // a bare cross-type name is non-generic (arity 0)
				val sym = when {
					cid == null -> null
					cid == owner.classId -> owner
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
