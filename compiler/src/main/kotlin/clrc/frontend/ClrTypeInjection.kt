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
// `typeParams` = method-level generic parameters (`SizeOf<T>()` -> ["T"]); empty for ordinary methods.
private class ClrMethod(val name: String, val returnType: String, val open: Boolean, val params: List<ClrParam>, val typeParams: List<String> = emptyList())
private class ClrProperty(val name: String, val type: String, val mutable: Boolean, val open: Boolean)
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
		var tparams = emptyList<String>()
		val methods = ArrayList<ClrMethod>(); val ctors = ArrayList<List<ClrParam>>()
		val props = ArrayList<ClrProperty>(); val events = ArrayList<ClrEvent>()
		var indexer: ClrIndexer? = null
		fun flush() { if (name.isNotEmpty()) types.add(ClrType(name, dotNet, isObject, isInterface, isAnnotation, isOpen, tparams, ArrayList(methods), ArrayList(ctors), ArrayList(props), ArrayList(events), indexer)) }
		for (raw in file.readLines()) {
			val line = raw.trim()
			if (line.isEmpty()) continue
			val tok = line.split(' ')
			when (tok[0]) {
				"package" -> {}   // ignored: types resolve at their real .NET namespace, not a synthetic package
				"object", "class", "interface" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null
					name = tok[1]; dotNet = tok[2]; isObject = tok[0] == "object"; isInterface = tok[0] == "interface"; isAnnotation = false
					isOpen = !isObject && !isInterface && tok.getOrNull(3) == "open"
					// `class <Name> <DotNet> <open|sealed> [<TypeParam>...]` -> trailing tokens are type params.
					tparams = if (tok[0] == "class") tok.drop(4) else emptyList()
				}
				// annotation <Name> <DotNet> [<param>:<type>]* — a .NET attribute -> Kotlin annotation class; the
				// trailing params (from its longest ctor) become the single annotation constructor.
				"annotation" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null
					name = tok[1]; dotNet = tok[2]; isObject = false; isInterface = false; isAnnotation = true; isOpen = false; tparams = emptyList()
					ctors.add(parseParams(tok.drop(3)))
				}
				// fun <Name> <ret> <open|final> [<TypeParam>...] [<param>:<type>]* — bare trailing tokens (no `:`)
				// are method type parameters; tokens with `:` are value params.
				"fun" -> methods.add(ClrMethod(tok[1], tok[2], tok.getOrNull(3) == "open",
					parseParams(tok.drop(4)), tok.drop(4).filterNot { it.contains(':') }))
				"ctor" -> ctors.add(parseParams(tok.drop(1)))
				// prop <Name> <type> <ro|rw> <open|final>
				"prop" -> props.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", tok.getOrNull(4) == "open"))
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
	// C-2: each .NET type resolves at its REAL namespace, so `import System.Text.StringBuilder` works through
	// Kotlin's normal package machinery — the .NET namespace IS the Kotlin package. (e.g.
	// "System.Text.StringBuilder" -> package "System.Text"; nested "+" and generic arity already stripped.)
	private fun namespaceOf(dotNet: String): String = dotNet.substringBefore('+').substringBeforeLast('.', "")
	private val byClassId: Map<ClassId, ClrType> =
		module?.types?.associateBy { ClassId(FqName(namespaceOf(it.dotNetName)), Name.identifier(it.kotlinName)) }.orEmpty()
	private val packages: Set<FqName> = byClassId.keys.map { it.packageFqName }.toSet()
	private val classIdByName: Map<String, ClassId> =
		byClassId.entries.associate { (id, t) -> t.kotlinName to id }

	// `__clrout(x)`/`__clrref(x)`: generic identity intrinsics (root package) marking a call arg as a .NET out/ref
	// param. The backend reads the marker and passes the lvalue's address with a `byref:` param type.
	private val intrinsicNames = setOf("__clrout", "__clrref")

	override fun hasPackage(packageFqName: FqName): Boolean =
		byClassId.isNotEmpty() && (packageFqName in packages || packageFqName.isRoot)

	override fun getTopLevelCallableIds(): Set<CallableId> =
		if (byClassId.isEmpty()) emptySet() else intrinsicNames.mapTo(HashSet()) { CallableId(FqName.ROOT, Name.identifier(it)) }

	override fun getTopLevelClassIds(): Set<ClassId> = byClassId.keys

	override fun generateTopLevelClassLikeDeclaration(classId: ClassId): FirClassLikeSymbol<*>? {
		val type = byClassId[classId] ?: return null
		val kind = when { type.isAnnotation -> ClassKind.ANNOTATION_CLASS; type.isObject -> ClassKind.OBJECT; type.isInterface -> ClassKind.INTERFACE; else -> ClassKind.CLASS }
		// A non-sealed .NET class is `open` so Kotlin can inherit it (the basis of framework-direct UI).
		return createTopLevelClass(classId, ClrGeneratedKey, kind) {
			if (type.open || type.isInterface) modality = Modality.OPEN
			// Generic .NET type (`Collection<T>`) -> declare its type parameters (invariant; bounds omitted).
			for (tp in type.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
		}.symbol
	}

	override fun getCallableNamesForClass(classSymbol: FirClassSymbol<*>, context: MemberGenerationContext): Set<Name> {
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
		val type = byClassId[owner.classId] ?: return emptyList()
		val prop = type.properties.firstOrNull { it.name == callableId.callableName.asString() } ?: return emptyList()
		// Property name == .NET name verbatim, so the backend emits `recv.<Name>` directly.
		return listOf(createMemberProperty(owner, ClrGeneratedKey, callableId.callableName, coneOf(prop.type, owner), !prop.mutable, false) {
			if (prop.open) modality = Modality.OPEN
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
			// Top-level intrinsics `__clrout`/`__clrref`: `fun <T> __clrout(x: T): T` (an identity marker).
			if (callableId.callableName.asString() in intrinsicNames) {
				val fn = createTopLevelFunction(ClrGeneratedKey, callableId,
					{ tps -> tps[0].symbol.constructType(emptyArray(), false) }) {
					typeParameter(Name.identifier("T"), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					valueParameter(Name.identifier("x"), { tps -> tps[0].symbol.constructType(emptyArray(), false) })
				}
				return listOf(fn.symbol)
			}
			return emptyList()
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
					if (type.isInterface) modality = Modality.ABSTRACT          // interface members: implement in Kotlin
					else if (m.open && !type.isObject) modality = Modality.OPEN // .NET virtual => overridable
					for (p in m.params) valueParameter(Name.identifier(p.name), coneOf(p.type, owner))
				}.symbol
			} else {
				// A generic .NET method (`SizeOf<T>()`, `As<T>(o): T`). Declare its method type parameters, then
				// resolve the return type and any T-typed value params against THOSE params (via the provider forms,
				// since the type params don't exist until the function is being built). The CLR has reified generics,
				// so the backend just emits a generic .NET method call (MakeGenericMethod) — see [[clr-not-jvm-discard-jvmisms]].
				createMemberFunction(owner, ClrGeneratedKey, callableId.callableName,
					{ tps -> coneOfMethod(m.returnType, owner, m.typeParams, tps) }) {
					if (m.open && !type.isObject) modality = Modality.OPEN
					for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
					for (p in m.params) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, owner, m.typeParams, tps) })
				}.symbol
			}
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
	private fun coneOf(typeName: String, owner: FirClassSymbol<*>): ConeKotlinType {
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
				val cid = classIdByName[typeName]
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

/** Registers [ClrTypeInjector] as a FIR class-generation extension. */
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
