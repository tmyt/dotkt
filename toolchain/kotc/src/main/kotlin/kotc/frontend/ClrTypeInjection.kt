@file:OptIn(
	org.jetbrains.kotlin.fir.extensions.FirExtensionApiInternals::class,
	org.jetbrains.kotlin.fir.extensions.ExperimentalTopLevelDeclarationsGenerationApi::class,
	org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
)

package kotc.frontend

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

private class ClrParam(val name: String, val type: String)
// `open`/`abstract` = .NET virtual/abstract (Kotlin OPEN/ABSTRACT modality, overridable); `protected` = .NET
// Family/FamORAssem (so a Kotlin subclass can override a protected virtual lifecycle method — feedback item 2).
// `typeParams` = method-level generic parameters (`SizeOf<T>()` -> ["T"]); empty for ordinary methods.
// `infix`/`operator`/`suspend` = Kotlin modifiers with no .NET analog, restored from a DotKt assembly's
// [KotlinFunction] (carried in the `fun`/`tlfun` modifier token as comma-suffixes). For a `suspend` fun the
// returnType is already the unwrapped result T (facadegen unwrapped the emitted Task<T>).
private class ClrMethod(val name: String, val returnType: String, val open: Boolean, val abstract: Boolean, val protected: Boolean, val params: List<ClrParam>, val typeParams: List<String> = emptyList(),
	val infix: Boolean = false, val operator: Boolean = false, val suspend: Boolean = false, val inline: Boolean = false, val ext: Boolean = false, val clrName: String? = null) {
	// gap ①: upper bounds of this method's own type params (`<T : Comparable<T>>`), keyed by param name. Filled from the
	// `mbound` lines that follow the `fun`/`tlfun` line (mutable so the line-by-line parser can append after construction).
	val typeParamBounds: MutableMap<String, MutableList<String>> = HashMap()
}
// A restored top-level Kotlin function: its package, the .NET file-facade class to call, and the function itself.
private class ClrTopLevel(val pkg: FqName, val fileClassDotNet: String, val fn: ClrMethod)
// N5: one file-class candidate for a top-level CallableId (`.NET file class` + the value-param arity range it covers).
// Several candidates under one CallableId = same-name same-package overloads across DIFFERENT source files; the backend
// disambiguates by the resolved callee's arity. See `clrInjectedTopLevelFileClass`.
internal class TopLevelSig(val fileClass: String, val minArity: Int, val maxArity: Int)
private class ClrTopLevelProp(val pkg: FqName, val fileClassDotNet: String, val name: String, val type: String, val mutable: Boolean, val receiver: String)
private class ClrProperty(val name: String, val type: String, val mutable: Boolean, val open: Boolean, val abstract: Boolean, val protected: Boolean, val clrName: String? = null)
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
	val baseNoArgCtor: Boolean,        // false ("basector none"): base lacks a no-arg ctor -> don't synthesize `: super()`
	val staticMethods: List<ClrMethod>,// public static methods of a NORMAL class -> companion-object members (App.Start)
	val staticProps: List<ClrProperty>,// public static props/fields of a NORMAL class -> companion-object members
	val memberExtProps: List<ClrMemberExtProp> = emptyList(),  // `class C { val T.p }` member extension properties
	val clrBinding: String? = null,    // ref/runtime split: the BCL type this Kotlin type binds to (`List` -> IReadOnlyList)
	val typeParamVariance: Map<String, String> = emptyMap(),   // gap ①: declaration-site variance (`out`/`in`) per type param (interfaces)
	val typeParamBounds: Map<String, List<String>> = emptyMap(), // gap ①: upper bound(s) per type param (`<T : Comparable<T>>`)
	val isFunInterface: Boolean = false,   // round-trip: was a Kotlin `fun interface` (SAM) — restore `status.isFun` so lambdas convert
	val isSealed: Boolean = false,         // round-trip: was a Kotlin `sealed` class/interface — restore Modality.SEALED
	val iteratorElem: String? = null,      // IEnumerable<T> element -> a frontend-only `operator fun iterator(): Iterator<T>` (for-in resolves; backend uses GetEnumerator)
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
 * A2 keystone (interop-no-registry, stage 1): the backend reads an injected .NET type's name off its IR `ClassId`
 * through this accessor — facadegen's metadata keyed by the resolved ClassId, with the generic-arity backtick stripped
 * (`System.Threading.Tasks.Task\`1` -> `Task`; ilemit re-appends `\`N` from the arg count). Replaces the deleted
 * name-keyed `ClrTypeRegistry.typeNames`. Null for a non-injected class (user Kotlin type / stdlib). File-top-level so
 * it can reach the file-private [ClrMetadataHolder] while exposing only public types across the module boundary.
 */
internal fun clrInjectedDotNetName(classId: ClassId): String? = ClrMetadataHolder.dotNetNameByClassId[classId]

/**
 * A2 keystone (interop-no-registry, stage 2): the backend reads an injected .NET MEMBER's slot name off its resolved IR
 * `CallableId` (declaring-class `ClassId` + member name) through this accessor — facadegen's metadata keyed by that same
 * structural identity. Replaces the deleted name-keyed `ClrTypeRegistry.memberNames`/`memberClrName`. Non-null only where
 * the .NET slot name DIVERGES from the Kotlin member name (a .NET operator method: `plus` -> `op_Addition`); null for a
 * member whose Kotlin name already IS its .NET name, and for any non-injected (user Kotlin / stdlib) member.
 */
internal fun clrInjectedMemberName(callableId: CallableId): String? = ClrMetadataHolder.memberClrNameByCallableId[callableId]

/**
 * A2 keystone (interop-no-registry, stage 3): the backend reads a restored DotKt TOP-LEVEL function's .NET file-facade
 * class (`LibKt`) off its resolved IR `CallableId` (`package` + name) through this accessor — facadegen's metadata keyed
 * by that same structural identity. Replaces the deleted name-keyed `ClrTopLevelRegistry` + its RECEIVER-DISCRIMINATOR
 * kludge: FIR/Fir2Ir already resolved the call to a UNIQUE callee, so there is nothing left to disambiguate — the resolved
 * callee's CallableId keys the fact directly (no candidate list, no "last-registered wins" receiver match). Null for a
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
 * facadegen's metadata keyed structurally. Replaces the deleted `ClrTopLevelRegistry.lookupProp` name-FQN string lookup.
 * Null for a non-restored top-level property.
 */
internal fun clrInjectedTopLevelPropFileClass(callableId: CallableId): String? = ClrMetadataHolder.fileClassByTopLevelPropCallableId[callableId]

/**
 * Loads the .NET type metadata to inject, once per process. The path comes from `CLR_TYPES_METADATA`
 * (set by the build / MSBuild / verify harness). Absent or empty => inject nothing, so compilations
 * that don't opt in are completely unaffected. The backend reads each injected type's .NET name off its
 * IR `ClassId` via [clrInjectedDotNetName] and each injected member's .NET slot name off its IR `CallableId`
 * via [clrInjectedMemberName] — keyed structurally off the resolved IR identity. A .NET EVENT no longer needs any
 * side-channel at all: it is surfaced as a `ClrEvent<T>` property and subscribed via `+=`/`-=`, which bir2cir binds
 * to the add/remove accessor (the `add_`/`remove_` accessor synthesis + its event-op map are RETIRED, 2026-07-05).
 * A2 interop-no-registry is COMPLETE: all four name-keyed side-channel registries are eliminated.
 */
private object ClrMetadataHolder {
	val module: ClrModule? by lazy { System.getenv("CLR_TYPES_METADATA")?.let { load(File(it)) } }

	private fun load(file: File): ClrModule? {
		if (!file.isFile) return null
		val types = ArrayList<ClrType>()
		var name = ""; var dotNet = ""; var isObject = false; var isInterface = false; var isOpen = false; var isAnnotation = false
		var isFunIface = false; var isSealedTy = false   // round-trip: `funinterface`/`sealed` marker lines for the CURRENT type
		var clrBind: String? = null   // ref/runtime split: BCL binding from `token[2] = KotlinFqn=BclName`
		var tparams = emptyList<String>(); var supers = emptyList<String>()
		val methods = ArrayList<ClrMethod>(); val ctors = ArrayList<List<ClrParam>>()
		val props = ArrayList<ClrProperty>(); val events = ArrayList<ClrEvent>()
		var indexer: ClrIndexer? = null
		var iteratorElem: String? = null
		var baseNoArgCtor = true
		val staticMethods = ArrayList<ClrMethod>(); val staticProps = ArrayList<ClrProperty>()
		val memberExtProps = ArrayList<ClrMemberExtProp>()
		val topLevel = ArrayList<ClrTopLevel>(); val topLevelProps = ArrayList<ClrTopLevelProp>(); var filePkg: FqName? = null; var fileClass = ""   // current [KotlinFile] section
			// gap ①: per-type-param variance/bounds accumulators for the CURRENT type (from `tvariance`/`tbound` lines);
			// `lastMethod` is the most-recently-parsed fun/tlfun, the target of any following `mbound` (method-level) line.
			val tpVariance = HashMap<String, String>(); val tpBounds = HashMap<String, MutableList<String>>(); var lastMethod: ClrMethod? = null
		fun flush() { if (name.isNotEmpty()) types.add(ClrType(name, dotNet, isObject, isInterface, isAnnotation, isOpen, tparams, supers, ArrayList(methods), ArrayList(ctors), ArrayList(props), ArrayList(events), indexer, baseNoArgCtor, ArrayList(staticMethods), ArrayList(staticProps), ArrayList(memberExtProps), clrBind, HashMap(tpVariance), tpBounds.mapValues { it.value.toList() }, isFunIface, isSealedTy, iteratorElem)); clrBind = null }
		for (raw in file.readLines()) {
			val line = raw.trim()
			if (line.isEmpty()) continue
			val tok = line.split(' ')
			when (tok[0]) {
				"package" -> {}   // ignored: types resolve at their real .NET namespace, not a synthetic package
				"object", "class", "interface" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; iteratorElem = null; baseNoArgCtor = true; staticMethods.clear(); staticProps.clear(); memberExtProps.clear(); supers = emptyList(); filePkg = null; tpVariance.clear(); tpBounds.clear(); lastMethod = null; isFunIface = false; isSealedTy = false
					// ref/runtime split: `token[2] = KotlinFqn=BclName` -> LEFT = Kotlin identity (drives namespace/ClassId),
					// RIGHT (if any) = the BCL binding for clrName. Most injected types have no `=`.
					val dn = tok[2]; val eq = dn.indexOf('='); dotNet = if (eq >= 0) dn.substring(0, eq) else dn; clrBind = if (eq >= 0) dn.substring(eq + 1) else null
					name = tok[1]; isObject = tok[0] == "object"; isInterface = tok[0] == "interface"; isAnnotation = false
					isOpen = !isObject && !isInterface && tok.getOrNull(3) == "open"
					// `class <Name> <DotNet> <open|sealed> [<TP>...]` (TPs at 4, after the modality token) vs
					// `interface <Name> <DotNet> [<TP>...]` (TPs at 3, no modality token). `object` has none.
					tparams = when (tok[0]) { "class" -> tok.drop(4); "interface" -> tok.drop(3); else -> emptyList() }
				}
				// annotation <Name> <DotNet> [<param>:<type>]* — a .NET attribute -> Kotlin annotation class; the
				// trailing params (from its longest ctor) become the single annotation constructor.
				"annotation" -> {
					flush(); methods.clear(); ctors.clear(); props.clear(); events.clear(); indexer = null; iteratorElem = null; baseNoArgCtor = true; staticMethods.clear(); staticProps.clear(); memberExtProps.clear(); supers = emptyList(); filePkg = null; tpVariance.clear(); tpBounds.clear(); lastMethod = null; isFunIface = false; isSealedTy = false
					name = tok[1]; dotNet = tok[2]; isObject = false; isInterface = false; isAnnotation = true; isOpen = false; tparams = emptyList()
					ctors.add(parseParams(tok.drop(3)))
				}
				// file <package> <fileClassFqn> — a Kotlin file facade ([KotlinFile]); subsequent `tlfun` lines are
				// TOP-LEVEL functions in <package> (`-` = root), restored as a .NET static call to <fileClassFqn>.
				"file" -> {
					flush(); name = ""; supers = emptyList(); tpVariance.clear(); tpBounds.clear(); lastMethod = null
					filePkg = if (tok.getOrNull(1) == "-" || tok.getOrNull(1).isNullOrEmpty()) FqName.ROOT else FqName(tok[1])
					fileClass = tok.getOrNull(2) ?: ""
				}
				// tlfun <Name> <ret> <prot-?open|final|abstract>[,infix][,operator][,suspend] [<TP>...] [<param>:<type>]*
				"tlfun" -> {
					val fm = parseFunMods(tok.getOrNull(3))
					val m = ClrMethod(tok[1], tok[2], fm.open, fm.abstract, fm.protected,
						parseParams(tok.drop(4)), tok.drop(4).filterNot { it.contains(':') }, fm.infix, fm.operator, fm.suspend, fm.inline, fm.ext)
					topLevel.add(ClrTopLevel(filePkg ?: FqName.ROOT, fileClass, m)); lastMethod = m   // gap ①: target of any following `mbound`
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
					val rest = tok.drop(4)   // ref/runtime split: pull the `clr:Name` member-binding token out before param/typeparam parsing
					val mclr = rest.firstOrNull { it.startsWith("clr:") }?.removePrefix("clr:")
					val body = rest.filterNot { it.startsWith("clr:") }
					methods.add(ClrMethod(tok[1], tok[2], fm.open, fm.abstract, fm.protected,
						parseParams(body), body.filterNot { it.contains(':') }, fm.infix, fm.operator, fm.suspend, fm.inline, fm.ext, mclr))
						lastMethod = methods.last()   // gap ①: target of any following `mbound`
				}
				"ctor" -> ctors.add(parseParams(tok.drop(1)))
				// sfun <Name> <ret> [<TypeParam>...] [<param>:<type>]* — a public STATIC method of a normal class (-> companion).
				// Bare trailing tokens (no `:`) are the method's own type parameters (`Task.FromResult<TResult>`), mirroring
				// the `fun`/`tlfun` convention — KEEP them so a generic static (Task.FromResult<T>/Run<T>) can build Task<T>.
				"sfun" -> { val rest = tok.drop(3)
					staticMethods.add(ClrMethod(tok[1], tok[2], false, false, false, parseParams(rest), rest.filterNot { it.contains(':') })) }
				// sprop <Name> <type> <ro|rw> — a public STATIC prop/field of a normal class (-> companion).
				"sprop" -> staticProps.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", false, false, false))
				// prop <Name> <type> <ro|rw> <prot-?open|final|abstract>
				"prop" -> {
					val mod = tok.getOrNull(4) ?: "final"; val prot = mod.startsWith("prot-"); val bare = mod.removePrefix("prot-")
					val pclr = tok.firstOrNull { it.startsWith("clr:") }?.removePrefix("clr:")   // ref/runtime split: size -> Count
					props.add(ClrProperty(tok[1], tok[2], tok.getOrNull(3) == "rw", bare == "open", bare == "abstract", prot, pclr))
				}
				// memextprop <Name> <type> <ro|rw> <receiverType> <prot-?final> — a member extension property `val T.p`
				"memextprop" -> memberExtProps.add(ClrMemberExtProp(tok[1], tok[2], tok.getOrNull(3) == "rw", tok[4], (tok.getOrNull(5) ?: "final").startsWith("prot-")))
				// event <Name> <handlerRet> <handlerParams...>
				"event" -> events.add(ClrEvent(tok[1], tok[2], parseParams(tok.drop(3))))
				// index <indexType> <valueType> <ro|rw> — `this[i]` indexer -> operator get/set.
				"index" -> indexer = ClrIndexer(tok[1], tok[2], tok.getOrNull(3) == "rw")
					// iterator <elem> — a type implementing IEnumerable<elem> -> a frontend-only `operator fun iterator(): Iterator<elem>`.
					"iterator" -> iteratorElem = tok.getOrNull(1)
					// round-trip: `funinterface` = the current interface was a Kotlin `fun interface` (restore status.isFun for SAM);
					// `sealed` = the current class/interface was Kotlin `sealed` (restore Modality.SEALED). Standalone marker lines.
					"funinterface" -> isFunIface = true
					"sealed" -> isSealedTy = true
					// gap 1: `tvariance <param> <out|in>` = declaration-site variance of a class/interface type param.
					"tvariance" -> if (tok.size >= 3) tpVariance[tok[1]] = tok[2]
					// gap 1: `tbound <param> <boundToken>` = an upper bound of a class/interface type param (repeatable).
					"tbound" -> if (tok.size >= 3) tpBounds.getOrPut(tok[1]) { ArrayList() }.add(tok[2])
					// gap 1: `mbound <param> <boundToken>` = an upper bound of the most-recent fun/tlfun method type param.
					"mbound" -> if (tok.size >= 3) lastMethod?.typeParamBounds?.getOrPut(tok[1]) { ArrayList() }?.add(tok[2])
			}
		}
		flush()
		val module = ClrModule(types, topLevel, topLevelProps)
		// A2 stage 3 / event redesign: NOTHING is registered by name here anymore. A restored top-level function/extension-
		// property's .NET file-facade class is read off the resolved IR `CallableId` (`fileClassByTopLevelCallableId` /
		// `fileClassByTopLevelPropCallableId`); a .NET event carries NO side-channel at all — it is surfaced as a `ClrEvent<T>`
		// property that bir2cir binds via the `+=`/`-=` operators. The old name-keyed side-tables (and the top-level
		// receiver-discriminator disambiguation) are gone. This eliminated the last of the four interop registries
		// (`ClrTypeRegistry` / `ClrTopLevelRegistry` / `ClrEventRegistry`).
		return module
	}

	private fun parseParams(tokens: List<String>): List<ClrParam> =
		tokens.filter { it.contains(':') }.map { ClrParam(it.substringBefore(':'), it.substringAfter(':')) }

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
	// facadegen-injected stdlib type never happens (kotlin.* comes from the JAR), so the old `clrBinding` fallback of the
	// deleted `ClrTypeRegistry.typeNames` was dead and is dropped. NOTE: the .NET name of an arity-QUALIFIED Kotlin name
	// (`Task\`1` -> Kotlin `Task1`) genuinely diverges from the ClassId simple name, so it must be carried (facadegen's
	// fact), not re-derived from the ClassId string — hence this metadata read rather than `classId.asString()` alone.
	val dotNetNameByClassId: Map<ClassId, String> by lazy { byClassId.mapValues { it.value.dotNetName.substringBefore('`') } }
	// A2 keystone (interop-no-registry stage 2): the backend's clrName reads an injected MEMBER's .NET slot name off its
	// resolved IR identity — its `CallableId` (declaring-class `ClassId` + member name) — instead of the deleted
	// `ClrTypeRegistry.memberNames` name-keyed side-table. This is facadegen's own metadata keyed by that same structural
	// CallableId; the value is the member's TRUE .NET slot name where it DIVERGES from the Kotlin name — the live case is a
	// .NET operator method (`plus` -> `op_Addition`, `unaryMinus` -> `op_UnaryNegation`) and accessor-renamed members. The
	// declaring-class ClassId is built exactly as `byClassId` builds a type's ClassId (`ns`/`kotlinName`), so it matches the
	// injected FIR/IR member's `CallableId`. Keyed off ALL `module.types` (mirroring the old registerMember loop): a
	// @Clr-bound stdlib type's member never actually reaches this lookup (kotlin.* comes from the JAR; the ref.meta is never
	// fed as CLR_TYPES_METADATA to an app build), but including it keeps the projection byte-identical to the deleted map.
	val memberClrNameByCallableId: Map<CallableId, String> by lazy {
		buildMap {
			for (t in module?.types.orEmpty()) {
				val classId = ClassId(FqName(namespaceOf(t.dotNetName)), Name.identifier(t.kotlinName))
				for (p in t.properties) p.clrName?.let { put(CallableId(classId, Name.identifier(p.name)), it) }
				for (m in t.methods) m.clrName?.let { put(CallableId(classId, Name.identifier(m.name)), it) }
			}
		}
	}
	// (RETIRED 2026-07-05) The `eventOpByCallableId` side-table (a synthesized `add_<E>`/`remove_<E>` accessor's
	// `(eventName, op)` fact) is GONE with the accessor-synthesis model: a .NET event is now surfaced as a `ClrEvent<T>`
	// property, subscribed via the idiomatic `+=`/`-=` operators, which bir2cir's ClrEventOperatorBinding binds to the
	// add/remove accessor from the plain operator call -- no name-keyed map, and no `add_`/`remove_` naming anywhere in kotc.
	// A2 keystone (interop-no-registry stage 3): a restored DotKt top-level function's .NET file-facade class keyed by its
	// resolved IR `CallableId` (`package`/name — exactly the CallableId the injector builds in `topLevelByCallable`, so it
	// matches the resolved Fir2Ir callee). This REPLACES the deleted `ClrTopLevelRegistry.funs` name-FQN -> [(fileClass,
	// recvDisc, suspend)] candidate list: because FIR already resolved every call to a UNIQUE callee, the list collapses to
	// a single fileClass per CallableId — no receiver discriminator, no "last-registered wins". The `Clr`-file-class strip
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
				val trailingOpt = vps.reversed().takeWhile { it.type.startsWith("opt:") }.count()
				getOrPut(CallableId(tl.pkg, Name.identifier(tl.fn.name))) { ArrayList() }
					.add(TopLevelSig(stripClrFileClass(tl.fileClassDotNet), vps.size - trailingOpt, vps.size))
			}
		}
	}
	// A2 keystone (interop-no-registry stage 3): a restored DotKt top-level EXTENSION PROPERTY's .NET file-facade class
	// (holding its `get_`/`set_<name>` statics) keyed by its resolved IR `CallableId`. Replaces `ClrTopLevelRegistry.props`.
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
 * assemblies — the same reflection that used to emit `.kt`, now feeding the compiler in-memory.
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
	// lvalue's address; `netType(ClrRef<T>)` is `byref:T`. byref/ClrRef live in the `kotlin.clr` namespace (the CLR-
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
	// `Span<T>`: a `kotlin.clr` intrinsic that maps to the real `System.Span<T>` (netType/birType -> clrg:System.Span)
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
				val variance = when (type.typeParamVariance[tp]) {
					"out" -> org.jetbrains.kotlin.types.Variance.OUT_VARIANCE
					"in" -> org.jetbrains.kotlin.types.Variance.IN_VARIANCE
					else -> org.jetbrains.kotlin.types.Variance.INVARIANT
				}
				typeParameter(Name.identifier(tp), variance, false, ClrGeneratedKey) {
					for (b in type.typeParamBounds[tp].orEmpty())
						bound { tps -> boundConeOf(b, type.typeParams, tps) ?: session.builtinTypes.nullableAnyType.coneType }
				}
			}
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
		}
		// A class with STATIC members: create its companion EAGERLY and LINK it (`replaceCompanionObjectSymbol`), so the
		// IMPLICIT form `App.Start(...)` resolves — the resolver types the bare qualifier off that link (upstream
		// ResolveUtils.kt:457 `typeForQualifierByDeclaration` -> `canBeValue`), and nothing in stock K2 sets it for a
		// fully-generated owner (FirCompanionGenerationProcessor only walks FirFiles). The link makes the framework's
		// nested-generation fallback unreachable (FirGeneratedScopes.kt:245-248 early-returns the linked companion before
		// the :255 `ownerGenerator` assignment), and generated-origin member lookup dies on `ownerGenerator!!`
		// (FirGeneratedScopes.kt:290) — so we set that attribute ourselves via [FirInternals]. The instance is cached:
		// generateNestedClassLikeDeclaration must return THIS companion (one symbol per ClassId, never a second one).
		if (type.staticMethods.isNotEmpty() || type.staticProps.isNotEmpty()) {
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
		// Same-instance invariant: the eager companion built (and linked) in generateTopLevelClassLikeDeclaration is
		// THE companion for this ClassId — never create a second one. (Reached only if the framework's early return on
		// the linked companionObjectSymbol — FirGeneratedScopes.kt:245-248 — didn't already answer the lookup.)
		eagerCompanions[owner.classId.createNestedClassId(name)]?.let { return it.symbol }
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
		// `ClrEvent<T>`: the `+=`/`-=` subscription operators (member operators — see Codex-verified resolution).
		if (classSymbol.classId == clrEventClassId)
			return hashSetOf(Name.identifier("plusAssign"), Name.identifier("minusAssign"))
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
		// A .NET event is surfaced as a read-only member PROPERTY `Changed: ClrEvent<HandlerFn>` (the idiomatic
		// `w.Changed += handler` subscription); the old `add_<E>`/`remove_<E>` accessor-method synthesis is retired.
		type.events.forEach { names.add(Name.identifier(it.name)) }
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
		// A .NET event -> a read-only member property `Changed: ClrEvent<HandlerFn>` (the `+=`/`-=` subscription handle).
		// No .NET property/field backs it: `w.Changed` is a compile-time handle whose read the backend emits as a plain
		// clrPropGet(owner .NET type, event name) — consumed by bir2cir's ClrEventOperatorBinding, never materialized. The
		// ClrEvent type arg is the handler's Kotlin FUNCTION type, so a lambda `{ s, e -> }` binds straight to `plusAssign(T)`.
		type.events.firstOrNull { it.name == callableId.callableName.asString() }?.let { ev ->
			val handler = coneFunctionType(ev.handlerParams.map { coneOf(it.type, owner) }, coneOf(ev.handlerReturn, owner))
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
			// lowered (construction is native clrNew) so the missing delegation is harmless.
			val real = realDefaults(params)   // all-buildable ctor defaults -> real defaults (`Pt(y = 4)` omits x); else required
			createConstructor(context.owner, ClrGeneratedKey, i == 0, type.baseNoArgCtor) {
				for (p in params) valueParameter(Name.identifier(p.name), coneOf(p.type, context.owner), hasDefaultValue = real && p.type.startsWith("opt:"))
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
			// so a top-level fun carries at most `suspend`; the backend (ClrTopLevelRegistry) emits the static call.
			topLevelByCallable[callableId]?.let { tls ->
				return tls.flatMap { tl ->
					val m = tl.fn
					// An extension fun: the first param `__self` is the receiver (rest are value params).
					val extRecv = if (m.ext && m.params.isNotEmpty()) m.params[0] else null
					val vps = if (extRecv != null) m.params.drop(1) else m.params
					val real = realDefaults(vps)
					val trailingOpt = if (real) 0 else vps.reversed().takeWhile { it.type.startsWith("opt:") }.count()
					((vps.size - trailingOpt)..vps.size).map { arity ->
						createTopLevelFunction(ClrGeneratedKey, callableId, { tps -> coneOfMethod(m.returnType, null, m.typeParams, tps) }) {
							for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey) {
								for (b in m.typeParamBounds[tp].orEmpty())   // gap ①: `<T : Comparable<T>>` bound on a top-level fun
									bound { tps -> boundConeOf(b, m.typeParams, tps) ?: session.builtinTypes.nullableAnyType.coneType }
							}
							if (m.suspend) status { isSuspend = true }
							if (m.inline) status { isInline = true }   // accept non-local return; ilemit splices the carried body
							if (m.infix || m.operator) status { isInfix = m.infix; isOperator = m.operator }   // top-level extension operators
							if (extRecv != null) extensionReceiverType { tps -> coneOfMethod(extRecv.type, null, m.typeParams, tps) }
							for (p in vps.take(arity))
								if (p.type.startsWith("vararg:")) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod("array:" + p.type.removePrefix("vararg:"), null, m.typeParams, tps) }, isVararg = true)
								else valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, null, m.typeParams, tps) }, hasDefaultValue = real && p.type.startsWith("opt:"))
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
						{ tps -> coneOfMethod(m.returnType, owner, m.typeParams, tps) }) {
						for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey)
						for (p in m.params) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, owner, m.typeParams, tps) })
					}.symbol
			}
		}
		val type = byClassId[owner.classId] ?: return emptyList()
		val callName = callableId.callableName.asString()

		// (RETIRED 2026-07-05) The synthesized `add_<E>`/`remove_<E>` event-accessor methods are GONE: a .NET event is now
		// surfaced as a `ClrEvent<T>` property (generateProperties) and subscribed via the idiomatic `w.<E> += handler` /
		// `-= handler` operators (ClrEvent.plusAssign/minusAssign above). The `add_`/`remove_` naming no longer exists.

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
				val trailingOpt = if (real) 0 else vps.reversed().takeWhile { it.type.startsWith("opt:") }.count()
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
						else valueParameter(Name.identifier(p.name), coneOf(p.type, owner), hasDefaultValue = real && p.type.startsWith("opt:"))
				}.also { if (real) applyDefaults(it, vps) }.symbol
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
					for (tp in m.typeParams) typeParameter(Name.identifier(tp), org.jetbrains.kotlin.types.Variance.INVARIANT, false, ClrGeneratedKey) {
						for (b in m.typeParamBounds[tp].orEmpty())   // gap ①: `<T : Comparable<T>>` bound on a member fun
							bound { tps -> boundConeOf(b, m.typeParams, tps) ?: session.builtinTypes.nullableAnyType.coneType }
					}
					if (extRecv != null) extensionReceiverType { tps -> coneOfMethod(extRecv.type, owner, m.typeParams, tps) }
					for (p in vps) valueParameter(Name.identifier(p.name), { tps -> coneOfMethod(p.type, owner, m.typeParams, tps) }, hasDefaultValue = realDefaults(vps) && p.type.startsWith("opt:"))
				}.also { if (realDefaults(vps)) applyDefaults(it, vps) }.symbol
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

	// gap ①: kotlin BUILTIN types that appear as generic-constraint targets but aren't in the injection metadata (the
	// jar/frontend owns them). The common one: a Kotlin `T : Comparable<T>` bound lowers to the CLR `System.IComparable<T>`,
	// which facadegen reverses to `generic:Comparable[T]` (MapBound). Resolve that back to the real kotlin.Comparable symbol.
	private val builtinBoundOpen: Map<String, ClassId> = mapOf(
		"Comparable" to ClassId(FqName("kotlin"), Name.identifier("Comparable")),
		"Number" to ClassId(FqName("kotlin"), Name.identifier("Number")),
		"Enum" to ClassId(FqName("kotlin"), Name.identifier("Enum")),
		"CharSequence" to ClassId(FqName("kotlin"), Name.identifier("CharSequence")),
	)

	/** gap ①: the ClassId of a bound token's OPEN class — an INJECTED type (by name+arity, or a fully-qualified ClassId),
	 *  or a well-known kotlin builtin. Null => resolves to nothing (the caller drops the bound, restoring an unconstrained
	 *  `T` exactly as before — never worse than the previous behavior). NOTE: returns a ClassId, NOT a resolved symbol —
	 *  the caller builds a LAZY lookup-tag cone from it (see boundConeOf), so it never eagerly resolves the symbol. */
	private fun boundClassId(open: String, arity: Int): ClassId? =
		ClrMetadataHolder.classIdFor(open, arity)
			?: if ('.' in open) ClassId(FqName(open.substringBeforeLast('.')), Name.identifier(open.substringAfterLast('.'))) else builtinBoundOpen[open]

	/** gap ①: resolve a generic-constraint bound token (`generic:Comparable[T]`, an injected type, or a bare name) to a
	 *  cone, binding a self-referential arg (`T`) to the declaring type/function's own type parameters (`tps`). Null =>
	 *  unresolvable (the caller falls back to `Any?`, i.e. no effective bound).
	 *
	 *  The open class is built as a LAZY lookup-tag cone (`ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(cid), ...)`),
	 *  NOT by resolving its symbol — the curiously-recurring BCL bounds (`TSelf : INumber<TSelf>`, the whole numeric
	 *  tower reachable from a `System.*` injection closure) reference types that are STILL BEING BUILT, so eagerly
	 *  resolving the symbol here re-enters generation and StackOverflows (the same hazard superArgCone documents). The
	 *  whole thing is also wrapped fail-soft: a pathological bound restores an unconstrained `T`, never a crash. */
	private fun boundConeOf(token: String, declaredParams: List<String>, tps: List<org.jetbrains.kotlin.fir.declarations.FirTypeParameterRef>): ConeKotlinType? {
		try {
			val nullable = token.endsWith("?"); val t = token.removeSuffix("?")
			val tv: (String) -> ConeKotlinType? = { name -> declaredParams.indexOf(name).let { i -> if (i in tps.indices) tps[i].symbol.constructType(emptyArray(), false) else null } }
			tv(t)?.let { return if (nullable) it.withNullability(true, session.typeContext) else it }   // a whole-bound type-param ref
			if (t.startsWith("generic:")) {
				val rest = t.removePrefix("generic:"); val br = rest.indexOf('[')
				val open = if (br < 0) rest else rest.substring(0, br)
				val inner = if (br < 0) "" else rest.substring(br + 1, rest.length - 1)
				val argToks = if (inner.isEmpty()) emptyList() else splitTopLevel(inner)
				val cid = boundClassId(open, argToks.size) ?: return null
				val args = argToks.map { coneOf(it, null, tv) }
				@Suppress("UNCHECKED_CAST")
				return ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(cid), args.toTypedArray() as Array<org.jetbrains.kotlin.fir.types.ConeTypeProjection>, nullable)
			}
			val cid = boundClassId(t, 0) ?: return null
			return ConeClassLikeTypeImpl(ConeClassLikeLookupTagImpl(cid), emptyArray(), nullable)
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

	/** Decode a meta-encoded default value (`\\`->`\`, `\s`->space, `\0`->NUL). */
	private fun decodeDefault(s: String): String {
		val sb = StringBuilder(); var i = 0
		while (i < s.length) {
			if (s[i] == '\\' && i + 1 < s.length) { when (s[i + 1]) { '\\' -> sb.append('\\'); 's' -> sb.append(' '); else -> sb.append(s[i + 1]) }; i += 2 }
			else { sb.append(s[i]); i++ }
		}
		return sb.toString()
	}

	/** `opt:Int=2` -> a `FirLiteralExpression(2)`, so a restored default arg has a REAL constant value fir2ir can inline
	 *  at the call site (the consumer may omit it ANYWHERE — trailing, named-middle `f(c=9)`, or reordered). */
	private fun optDefault(optType: String): FirExpression? {
		if (!optType.startsWith("opt:")) return null
		val rest = optType.removePrefix("opt:"); val eq = rest.indexOf('='); if (eq < 0) return null
		val ty = rest.substring(0, eq); val raw = rest.substring(eq + 1)
		if (raw == "\\0") return buildLiteralExpression(null, ConstantValueKind.Null, null, setType = true)
		val v = decodeDefault(raw)
		val (kind, value) = when (ty) {
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
		fn.valueParameters.forEachIndexed { i, vp -> if (i < params.size) optDefault(params[i].type)?.let { vp.replaceDefaultValue(it) } }
	}

	/** True when EVERY default-arg (`opt:`) param has a buildable constant default — then the function/ctor is restored
	 *  as ONE function with real defaults (the consumer may omit ANY default arg: trailing/named-middle/reordered).
	 *  A .NET BCL method with an enum/struct default (`NumberStyles = 7`) isn't buildable -> @JvmOverloads fallback
	 *  (trailing-omission overloads; ilemit fills the .NET default at the call site). Setting `hasDefaultValue` without a
	 *  real literal crashes fir2ir, so the two strategies must not mix on one function. */
	private fun realDefaults(params: List<ClrParam>): Boolean =
		params.all { !it.type.startsWith("opt:") || optDefault(it.type) != null }

	// `tv` resolves a bare type-variable name (a method/function type parameter) to its cone type; null when the name
	// isn't one. Threaded through every recursion so a `T` nested in `generic:Box[T]`/`array:T`/`func:…` also binds.
	private fun coneOf(typeName: String, owner: FirClassSymbol<*>?, tv: ((String) -> ConeKotlinType?)? = null): ConeKotlinType {
		// A trailing `?` -> the Kotlin nullable form `T?` (so a consumer can pass/handle null). From .NET NRT metadata.
		if (typeName.endsWith("?")) return coneOf(typeName.dropLast(1), owner, tv).withNullability(true, session.typeContext)
		// A trailing `!` -> a Kotlin PLATFORM (flexible) type `T!` = (T..T?): the .NET reference type carried NO nullability
		// metadata (an assembly that never opted into NRT), so we neither force non-null nor nullable — the consumer
		// decides, exactly as Kotlin/JVM treats un-annotated Java. Modeled as ConeFlexibleType(lower = T, upper = T?).
		if (typeName.endsWith("!")) {
			val lower = coneOf(typeName.dropLast(1), owner, tv)
			val upper = lower.withNullability(true, session.typeContext)
			if (lower is ConeRigidType && upper is ConeRigidType) return ConeFlexibleType(lower, upper, false)
			return lower
		}
		// `opt:T=<const>` marks a default-arg param: the type is T (the `=<const>` default value is applied separately
		// via applyDefaults -> replaceDefaultValue). Strip both the prefix and the trailing `=<const>`.
		if (typeName.startsWith("opt:")) return coneOf(typeName.removePrefix("opt:").substringBefore('='), owner, tv)
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
 * registrar. Wired into [kotc.pipeline.ClrCliPipeline] via `COMPILER_PLUGIN_REGISTRARS`.
 */
class ClrCompilerPluginRegistrar : CompilerPluginRegistrar() {
	override val supportsK2: Boolean = true
	override fun ExtensionStorage.registerExtensions(configuration: CompilerConfiguration) {
		FirExtensionRegistrarAdapter.registerExtension(ClrFirExtensionRegistrar())
	}
}
