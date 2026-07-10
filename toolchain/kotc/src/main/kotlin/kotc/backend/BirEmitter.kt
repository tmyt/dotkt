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

/**
 * D1.1 — Backend IR (BIR) emitter.
 *
 * Serializes a file to a compact JSON (BIR) that the `tools/ilemit` tool consumes to emit CIL directly.
 * This IR walk renders a structured AST as JSON; stack lowering is deferred to ilemit.
 *
 * Scope (M0): top-level functions; const/local/binop/unop/call/concat/ternary; var/set/return/
 * while/if. Classes & interop are later milestones (D1.4+).
 */
@OptIn(UnsafeDuringIrConstructionAPI::class)
class BirEmitter(private val messageCollector: MessageCollector? = null) {

	// Diagnostics: a construct the .NET backend can't lower yet is a COMPILE-TIME error with source location
	// (file:line:col) — never a silent BIR node that crashes ilemit later. `hadError` fails the build.
	var hadError = false; private set
	internal var fileEntry: IrFileEntry? = null

	internal fun locationOf(node: IrElement?): CompilerMessageLocation? {
		val fe = fileEntry ?: return null
		val off = node?.startOffset ?: return CompilerMessageLocation.create(fe.name)
		if (off < 0) return CompilerMessageLocation.create(fe.name)
		val lc = fe.getLineAndColumnNumbers(off)
		return CompilerMessageLocation.create(fe.name, lc.line, lc.column, null)
	}

	/**
	 * Report an unsupported Kotlin construct as a clear, source-located compile error and return a placeholder
	 * BIR node. The build fails (hadError), so this placeholder never reaches ilemit. `what` names the construct;
	 * `detail` is a plain-language explanation of why / what to do — NOT the word "deferred".
	 */
	// kotc emits ONE BIR independent of the ref/rt split. The ref/rt divergence (BCL substitution, the
	// kotlin.Comparable-bound + `in`-variance drops, the metadata strip) is entirely bir2cir's + ilemit's, keyed off the
	// `--build-stdlib=metadata|runtime` flag downstream. The stdlib REFERENCE build and the RUNTIME build produce
	// BIT-IDENTICAL BIR from a single kotc frontend run. docs/design-clr-stdlib-ref-runtime-split.md.

	internal fun unsupported(node: IrElement?, what: String, detail: String): String {
		hadError = true
		messageCollector?.report(CompilerMessageSeverity.ERROR,
			"the .NET backend does not support $what yet: $detail", locationOf(node))
		return """{"k":"unsupportedExpr","of":${str("$what — $detail")}}"""
	}

	// A `kotlin.clr.ClrEvent<T>` value is a compile-time-only fiction (the surfaced form of a .NET event); it may
	// appear ONLY as the receiver of a `+=`/`-=` subscription, never be materialized as a real value. This flag is
	// set true ONLY while emitting the event member-access that is the receiver of a ClrEvent `plusAssign`/`minusAssign`;
	// a ClrEvent-typed property read seen with it FALSE is a misuse (`val e = w.Changed`) and is a compile error.
	private var clrEventReceiverOk = false
	private inline fun <R> asClrEventReceiver(body: () -> R): R {
		val prev = clrEventReceiverOk; clrEventReceiverOk = true
		try { return body() } finally { clrEventReceiverOk = prev }
	}

	// Inline substitutions for synthetic temporaries (e.g. a `when` subject) in expression position.
	internal val valSubst = HashMap<String, String>()
	// Subset of `valSubst` keys whose substitution ALREADY yields the bare non-null VALUE of a value-type-nullable
	// (`Int?`) — e.g. a `SAFE_CALL` receiver bound to `Nullable<T>.Value`. The value-nullable unwrap helpers
	// (coerceValue / argExpr) must NOT re-wrap such a read, else the `.Value` is unwrapped twice
	// (`n?.plus(1)` gave 1 instead of 8). Registered/cleared alongside the corresponding valSubst entry.
	internal val valSubstUnwrapped = HashSet<String>()
	// While splicing an inline fun / inlined-lambda body: the SPLICED target's own `return`s must NOT emit as raw
	// method returns (the splice is a valueBlock INSIDE the caller). Maps the return target -> (result local or
	// null-for-unit, end label id); stmt(IrReturn) rewrites to `res = v; goto end`. See spliceBodyWithReturns.
	internal val inlineReturnSubst = HashMap<org.jetbrains.kotlin.ir.symbols.IrReturnTargetSymbol, Pair<String?, Int>>()
	// While splicing an `inline fun` body: its type PARAM (the IrTypeParameter itself, NOT its name — a name-keyed
	// map cross-captured an OUTER function's same-named param: let<T,R:=Unit> spliced inside mapNotNullTo<T,R>
	// rewrote the OUTER `R` to kotlin.Unit) -> the call's substituted type-argument BIR (see birType).
	internal val typeArgSubst = HashMap<IrTypeParameter, TypeNode>()

	// Lambda lifting: non-capturing lambdas become named static methods appended to the file class;
	// capturing lambdas become synthesized closure classes appended to the file's types.
	internal val liftedMethods = ArrayList<String>()
	internal val liftedTypes = ArrayList<String>()
	// Building the stdlib ITSELF: emit kotlin.* REFERENCE types (List/Set/Map/Iterable/Iterator/Map.Entry) as their real
	// kotlin.* types, NOT lowered to the BCL — the BCL substitution is the consuming APP's emit-time job (driven by the
	// @Clr metadata). Value-type primitives (Int/Bool/Char/Unit/Nothing/String) stay compiler-intrinsic either way.
	internal var lambdaCounter = 0
	internal var closureCounter = 0
	// CFG block-IR (E-0.5): file-global unique label ids (never reset) so ids never collide across methods/lambdas.
	internal var cfgLabelN = 0
	internal fun cfgFresh(): Int = cfgLabelN++
	// Inlining ([[function-inlining-spike]]): lambda params currently being inlined -> the lambda passed for them.
	internal val inlineLambdas = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration, IrFunctionExpression>()
	internal data class TypeArgScope(val keys: List<IrTypeParameter>, val old: Map<IrTypeParameter, TypeNode?>, val had: Set<IrTypeParameter>)
	internal val inlineLambdaTypeScopes = java.util.IdentityHashMap<IrFunctionExpression, TypeArgScope>()
	internal var inlCounter = 0
	internal var scopeCounter = 0
	internal var fileClass = ""   // current file's static class name (for top-level property access)
	// Per-file prefix for SYNTHETIC type names (closures, ref cells, sequence SMs). Each file is compiled by its own
	// BirEmitter with a fresh `closureCounter`, so unprefixed names like `dotkt$Closure0` COLLIDE across files when
	// ilemit links all BIR into one assembly (the dup overwrites in `_types` -> orphaned TypeBuilder -> Save crash).
	// `fileClass` is unique per file, so it disambiguates. Stays under the `dotkt$` prefix (ilemit marks those).
	internal val synthScope: String get() = fileClass.replace(Regex("[^A-Za-z0-9]"), "_")
	/** The `<File>Kt` class name of a top-level declaration's DEFINING file (so cross-file top-level property
	 *  access targets the owning file class, not whichever file is being emitted). */
	internal fun fileClassOf(decl: org.jetbrains.kotlin.ir.declarations.IrDeclaration): String {
		val f = decl.parent as? IrFile ?: return fileClass
		return fileClassName(f)
	}
	// The `<File>Kt` facade class name, qualified with the file's package as the .NET namespace (so top-level
	// declarations live in the package's namespace, and two same-named files in different packages don't collide).
	internal fun fileClassName(f: IrFile): String {
		var stem = File(f.fileEntry.name).name.removeSuffix(".kt")
		// Platform-actual files are named `<Common>Clr.kt` (e.g. _ComparisonsClr.kt); their `actual`s belong to the SAME
		// file class as the common expect (_ComparisonsKt) -- JVM merges expect/actual into one class. Strip the `Clr`
		// suffix so the actual lands in the common's class (ilemit then MERGES the two same-file-class inputs). Without
		// this, `actual inline fun maxOf(Int,Int)` lands in _ComparisonsClrKt while the call targets _ComparisonsKt.
		if (stem.endsWith("Clr")) stem = stem.dropLast(3)
		val base = stem.replaceFirstChar { it.uppercaseChar() } + "Kt"
		val pkg = f.packageFqName.asString()
		return if (pkg.isEmpty()) base else "$pkg.$base"
	}
	// Local functions: lifted to file-class statics; captured vars become leading params (calls prepend them).
	internal val localFns = HashMap<org.jetbrains.kotlin.ir.declarations.IrFunction, Triple<String, List<IrValueDeclaration>, List<IrTypeParameter>>>()

	// Anonymous objects (`object : I { }`) are lifted to synthetic top-level classes. Their IR name is
	// "<no name provided>" (not a valid IL identifier), so map the IrClass identity -> its assigned name;
	// every self-reference (ownerType / `@<no name>` type) is routed through `typeName`.
	internal val anonNames = java.util.IdentityHashMap<IrClass, String>()
	// Captured outer values inside a capturing object literal -> `this.<field>`. Keyed by value-declaration
	// IDENTITY (not name): the anon's own `<this>` and a captured outer `<this>` share the name "<this>".
	internal val captureSubst = java.util.IdentityHashMap<IrValueDeclaration, String>()
	// An extension-function `__self` receiver -> the `__self` arg. Keyed by IDENTITY: in a MEMBER extension
	// (`class C { fun T.f() }`) the extension receiver and the dispatch receiver BOTH have name "<this>", so a
	// name-keyed map can't tell them apart (it would capture C's `this` too). The dispatch `<this>` then falls
	// through to `{"k":"this"}` and the extension receiver resolves here.
	internal val selfSubst = java.util.IdentityHashMap<IrValueDeclaration, String>()
	// Function-local classes lifted to top-level synthetic types: the outer locals they capture (prepended to the
	// ctor at construction sites). Keyed by the IrClass.
	internal val localClassCaptures = java.util.IdentityHashMap<IrClass, List<IrValueDeclaration>>()
	// A lifted anon-object / local class that captures ENCLOSING generic type parameters: the `gp:`-token names it was
	// made generic over (detected by typeDef from its own rendered members). The construction site brackets these onto
	// the constructed type (`dotkt$objN[gp:T]`) so ilemit instantiates it with the enclosing args. Keyed by the IrClass.
	internal val liftedTypeArgNames = java.util.IdentityHashMap<IrClass, List<String>>()
	// The captured enclosing type-PARAMETERS (the actual IrTypeParameter symbols, in declaration order) that a lifted
	// anonymous-object class is made generic over. Parallel to liftedTypeArgNames; the construction site (blockExpr)
	// renders each through birType so the enclosing-scope `tv` (method/type) is emitted structurally.
	internal val liftedTypeArgParams = java.util.IdentityHashMap<IrClass, List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>>()
	// A local delegated property's getter/setter function -> the IrLocalDelegatedProperty, so call() rewrites a
	// `<get-x>`/`<set-x>` call to access on the delegate local (mirrors the member-property delegate path).
	internal val localDelegates = java.util.IdentityHashMap<IrSimpleFunction, IrLocalDelegatedProperty>()
	// The `buf` parameter of an active `stackBuffer { buf -> … }` block -> its stack allocation (ptr local + length
	// local + element type), so `buf[i]`/`buf[i]=v`/`buf.size` rewrite to stack ops while the block is spliced.
	internal class StackBufInfo(val ptrName: String, val lenName: String, val elemT: TypeNode)
	internal val stackBufSubst = java.util.IdentityHashMap<IrValueDeclaration, StackBufInfo>()
	// The Kotlin iterator protocol (`(Mutable)Iterator/Iterable<E>`) is NOT monomorphized: kotc emits the REAL
	// generic identity — `kotlin.collections.Iterator<E>` (a real emitted stdlib interface) / `Iterable<E>`
	// (@ClrTypeAlias'd by bir2cir to `System.Collections.Generic.IEnumerable<E>`, whose GetEnumerator ilemit's
	// reverse bridge synthesizes from the class's `iterator()`). A user `class R : Iterable<Int>` supertype, a
	// `for (x in r)`, and every `it.hasNext()`/`it.next()` all dispatch on that real generic — exactly as `by lazy`
	// dispatches on real `kotlin.Lazy<T>` (#57). The real generic interface is used (the BCL is full of them, ilemit
	// emits the stdlib's own, and `Lazy<T>` proves a value-type arg works).
	// A custom (non-lazy) delegated property passes a `KProperty<*>` to getValue/setValue. `kotlin.reflect.KProperty`
	// is now a REAL emitted stdlib interface (klib migration, #70) — the accessor's compiler-synthesized argument
	// materializes as `kotlin.reflect.ClrPropertyStub` (a real rt-stdlib name-only impl: `.name` + empty
	// `.annotations`, never get()/set()/invoke()), and a genuine `::prop` callable reference materializes a real
	// KProperty0/KMutableProperty0/KProperty1/KMutableProperty1 implementation (kotc's `propertyRef`, a lifted
	// class like `samConversion`'s). No more `dotkt$KProperty` synthetic identity.

	/** A user/anon class's emitted name (anon "<no name provided>" -> its synthetic lifted name). */
	// A user type's .NET name = its Kotlin package projected as the .NET namespace (`alpha.Box`), so classes with the
	// same simple name in different packages don't collide in the assembly (they did — they all flattened to the root
	// namespace). NESTED types stay simple-named (their outer carries the namespace); anon/synthetic names are already
	// unique. Root-package types are unchanged (fqName has no dot), so existing code is unaffected. birType references
	// user types through here, so the def name and every reference stay consistent.
	internal fun typeName(k: IrClass): String =
		// A companion: a PLAIN one flattens to the outer class's name (its members are the outer's statics); a
		// super-typed one (`companion object X : Base()`) is a lifted singleton `<Outer>.InstanceClass`. This must be a
		// rule in typeName (not just an anonNames entry) so a CROSS-FILE reference to the companion-as-value resolves to
		// the same lifted name everywhere, not only in the file that declares it.
		anonNames[k] ?: if (k.isCompanion && k.parent is IrClass)
			(if (k.superTypes.any { st -> val sk = st.classifierOrNull?.owner as? IrClass; sk != null && sk.fqNameWhenAvailable?.asString() != "kotlin.Any" })
				companionObjectTypeName(k) else typeName(k.parent as IrClass))
		else if (k.parent is IrClass) {
			val p = k.parent as IrClass
			val owner = if (p.isCompanion) p.parent as? IrClass else p
			// A type nested in a GENERIC enclosing flattens to a top-level type (PersistedAssemblyBuilder NREs on nested
			// generics — see the nestedIn suppression). Joining with `.` would put it in a namespace equal to the
			// enclosing type's name (`kotlin.collections.AbstractList` type AND namespace) -> the loader can't resolve the
			// base. Join with `$` (valid in a type name, NOT a namespace separator) to avoid the type/namespace collision.
			val sep = if (owner != null && owner.typeParameters.isNotEmpty()) "$" else "."
			(owner?.let { typeName(it) + sep } ?: "") + k.name.asString()
		}
		else (k.fqNameWhenAvailable?.asString() ?: k.name.asString())

	internal fun emittedNestedParent(k: IrClass): IrClass? {
		val p = k.parent as? IrClass ?: return null
		return if (p.isCompanion) p.parent as? IrClass else p
	}

	/** A `companion object X : Base()` whose companion has a real supertype (a class base or interface, not just `Any`).
	 *  Such a companion can't flatten to its (often abstract) parent's statics — its overrides would land on the
	 *  abstract parent. It is instead emitted as a concrete lifted singleton `<Outer>.InstanceClass` (an object, so it
	 *  carries its own static `INSTANCE`); the parent keeps none of its members. A plain companion (no supertype) still
	 *  flattens to the parent's statics. Returns the companion, or null. */
	internal fun superTypedCompanion(klass: IrClass): IrClass? =
		klass.declarations.filterIsInstance<IrClass>().firstOrNull { c ->
			c.isCompanion && c.superTypes.any { st ->
				val k = st.classifierOrNull?.owner as? IrClass
				k != null && k.fqNameWhenAvailable?.asString() != "kotlin.Any"
			}
		}

	/** The lifted singleton type name for a super-typed companion: `<Outer>.<CompanionName>CompanionObject`
	 *  (e.g. `kotlin.random.Random.DefaultCompanionObject`). */
	internal fun companionObjectTypeName(comp: IrClass): String =
		typeName(comp.parent as IrClass) + "." + comp.name.asString() + "CompanionObject"

	// #70: `kotlin.reflect.KProperty*` is a REAL emitted stdlib interface (KPropertyClr.kt), not a kotc synthetic —
	// `kPropertyStub`/`propertyRef` below materialize real implementations of it directly (no bir2cir-synthesized
	// type; the interface + its impls all live in the stdlib jar/ref/rt like any other real Kotlin type).

	// kotlin.CharSequence has no faithful .NET equivalent (it's a read-only INDEXED polymorphic char view — neither
	// IEnumerable<char>, char[], nor IReadOnlyList<char> fits, and String doesn't implement any of them as a common
	// supertype). So a user `class S : CharSequence` gets a synthetic monomorphized interface `dotkt$CharSequence`
	// (length getter + get(i) operator + subSequence). To pass a .NET string API, call `.toString()`.
	// #52/#68 (kotc-purity): kotc emits ONLY the plain Kotlin identity `kotlin.CharSequence` at a use site (a supertype in
	// a `class S : CharSequence`, or a CharSequence-typed param/local/return) — NO CLR synthetic name, NO `<>` marker.
	// bir2cir SUBSTITUTES `kotlin.CharSequence` -> its synthesized `dotkt_CharSequence` interface (SharedSyntheticSynthesis
	// owns the fixed-shape TYPE definition), exactly as it substitutes `kotlin.String` -> `System.String`.
	internal fun charSeqIface(t: IrType): String? =
		if (t.classFqName?.asString() == "kotlin.CharSequence") "kotlin.CharSequence" else null

	// A `kotlin.properties.Read(Write)Property<T,V>`-typed delegate is NOT monomorphized: kotc emits the REAL
	// generic stdlib interface identity (like `by lazy`'s `kotlin.Lazy<T>`), so delegate field/local types,
	// the `Delegates.observable(…)` value, and the getValue/setValue dispatch owner share one type (ilverify-clean).

	// heap ref-cell: local `var`s captured-and-mutated by a (non-inline) closure / object / local class are promoted
	// to a shared `dotkt$Ref<T>{ var v }` so the mutation is visible across the capture boundary. Per top-level
	// function (set in `method`/`ctor`); all reads/writes of such a var go through `.v`.
	internal var refCellVars: Set<IrValueDeclaration> = emptySet()
	internal val refTypes = LinkedHashMap<String, String>()   // element type JSON -> monomorphized Ref class name
	internal fun refTypeName(d: IrValueDeclaration): String {
		val elem = birType(d.type)
		return refTypes.getOrPut(elem.toJson()) { "dotkt\$${synthScope}\$Ref\$" + mangle(elem) }
	}
	// #52 (kotc-purity): the monomorphized heap cell `dotkt$Ref_<elem>{ var v }` is a CLR-representation synthetic.
	// kotc emits ONLY the FACT — a file-level `refTypes` registry (each cell's name + element TYPE identity) plus the
	// use-site `new`/`field`/`setField` on the cell. bir2cir's RefCellSynthesis assembles the actual trivial class
	// (single `v` field + its init ctor) into the file `types` from this registry. The element type is unrecoverable
	// from the use-site nodes alone (a bare `field .v` read carries no type), so the registry is the required fact.
	internal fun refTypesJson(): String = refTypes.entries.joinToString(",") { (elemJson, name) ->
		"""{"name":${str(name)},"elem":$elemJson}"""
	}
	internal fun isRefCell(d: IrValueDeclaration) = d in refCellVars
	/** The Ref-typed base expression for a ref-cell var: its capture field inside a closure, else the local. */
	internal fun refBase(d: IrValueDeclaration) = captureSubst[d] ?: """{"k":"local","name":${str(d.name.asString())}}"""
	/** A captured value's type as held in the closure: the Ref cell for a ref-cell var, else its plain type. */
	internal fun captureFieldType(d: IrValueDeclaration): TypeNode = if (isRefCell(d)) TypeNode.Fqn(refTypeName(d)) else birType(d.type)

	/** Local `var`s captured AND mutated by a closure/object/local class within [node] (-> need a heap ref-cell). */
	internal fun computeRefCells(node: IrElement): Set<IrValueDeclaration> {
		val out = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
		node.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				val caps: List<IrValueDeclaration>? = when (element) {
					is IrClass -> capturedVarsForObject(element)
					is IrFunctionExpression -> capturedVars(element.function)
					else -> null
				}
				if (caps != null) {
					val muts = mutatedIn(element)
					out.addAll(caps.filter { it is IrVariable && it.isVar && it in muts })
				}
				element.acceptChildrenVoid(this)
			}
		})
		return out
	}

	internal val SCOPE_FUNCTIONS = setOf("kotlin.let", "kotlin.run", "kotlin.with", "kotlin.apply", "kotlin.also")

	/** A scope-function call (`with(c){…}`, `c.let/run/apply/also {…}`) -> (fqName, receiver, lambda). These are INLINE,
	 *  so a suspension in the lambda body is the ENCLOSING coroutine's — `containsSuspend` descends into it for the fact. */
	internal fun scopeCall(e: org.jetbrains.kotlin.ir.IrElement?): Triple<String, IrExpression, IrFunctionExpression>? {
		val call = e as? IrCall ?: return null
		val fq = call.symbol.owner.fqNameWhenAvailable?.asString() ?: return null
		if (fq !in SCOPE_FUNCTIONS) return null
		val isWith = fq == "kotlin.with"
		val recv = (if (isWith) regularArgs(call).getOrNull(0) else extensionReceiver(call)) ?: return null
		val lambda = (if (isWith) regularArgs(call).getOrNull(1) else regularArgs(call).getOrNull(0)) as? IrFunctionExpression ?: return null
		return Triple(fq, recv, lambda)
	}

	// CLR-bound (@ClrTypeAlias) TYPE-STRIP is bir2cir's — kotc reads NEITHER @ClrTypeAlias NOR @ClrIntrinsic.
	// A @ClrTypeAlias class/interface/primitive (kotlin.Int, kotlin.collections.List, kotlin.text.StringBuilder, …) is
	// substituted to a BCL type at every use and must NOT be emitted as a real CLR type in the rt/app build. kotc emits
	// EVERY type as ordinary Kotlin, and bir2cir's AliasHelperHoist
	// (driven by the ref.dll @ClrTypeAlias index) DROPS the alias type def (hoisting a class's rule-3 members into the
	// dotkt$ClrH_* helper; an interface/object alias is dropped with no helper). The drop is a no-op in the REFERENCE
	// build (AliasHelperHoist is skipped there), so the ref assembly keeps the pure-Kotlin @ClrTypeAlias shapes verbatim.
	fun emitFile(file: IrFile): String {
		fileEntry = file.fileEntry
		// Per-FILE lifted state. One BirEmitter instance processes every file in turn, so these MUST be reset here —
		// otherwise each file's BIR accumulates the previous files' lifted lambdas/types, duplicating them into every
		// file class (e.g. App.kt's `__lambda*` reappearing in ControlsKt/DslKt/…). The `dotkt$*` types are
		// de-duplicated by ilemit, but lifted `__lambdaN` are file-class methods that are NOT — so the duplication is
		// real metadata bloat and a correctness hazard.
		liftedMethods.clear(); liftedTypes.clear(); refTypes.clear()
		// The `@ClrAwait` await intrinsic (`fun <T> Task<T>.await(): T`) is never emitted as a real method —
		// a suspend call site is flagged with `"suspendCall":true` and lowered by the deferred downstream layer. Skip it.
		// The `byref` out/ref marker is an intrinsic consumed at its call sites (the arg becomes a `byref:` param) —
		// never emitted as a real method.
		// Only USER functions (origin DEFINED) — a consuming module's FIR also holds plugin-INJECTED top-level funs
		// (stdlib ops restored from a referenced DotKt.Stdlib, in the synthetic `__GENERATED DECLARATIONS__` file);
		// those are the library's to provide, not ours to re-emit (a re-emitted stub has no real body -> invalid IL).
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && !isAwaitIntrinsic(it) && clrName(it) == null && it.name.asString() !in setOf("byref", "stackBuffer") }
		// `ClrRef<T>` is an intrinsic managed-reference marker (erased on the argument path) -> never emitted as a class.
		// @ClrTypeAlias classes (collections/StringBuilder/unsigned/primitives/String/…) are emitted here as ORDINARY
		// types; bir2cir's AliasHelperHoist drops them (and hoists a class's rule-3 members). kotc no longer strips them.
		// facadegen-INJECTED external .NET types (a `import P.Calc`/`P.SpanOps` host type, an inherited/implemented .NET
		// base) enter the FIR via CLR_TYPES_METADATA in the synthetic `__GENERATED DECLARATIONS__` file with a PLUGIN
		// origin (ClrGeneratedKey), NOT origin DEFINED. They are REFERENCED types (resolved via --ref), never ours to
		// emit — a re-emitted stub (empty ctor / a bogus `INSTANCE` singleton) collides with the referenced type and
		// crashes ilemit (Save "not created" / newobj on a ctor-less type). So filter every type bucket to origin
		// DEFINED, exactly as `functions`/`topProps` above already exclude the injected top-level MEMBERS. (@ClrTypeAlias
		// stdlib types are origin DEFINED in the stdlib build and thus kept; in an app build they come from the -classpath
		// jar and are not re-declared here at all.)
		val userDefined: (IrClass) -> Boolean = { it.origin.toString() == "DEFINED" }
		// The 4 unsigned specialized array value classes (`UByteArray`/`UShortArray`/`UIntArray`/`ULongArray`) live in
		// the stdlib source (libraries/stdlib/unsigned/src), so unlike the signed `IntArray` builtins they reach kotc —
		// but as of #76 they are a native CLR array family EXACTLY like `IntArray` (kotc emits the faithful FQN, bir2cir
		// decomposes to `Array(elem)`). A native array is NEVER emitted as a type, so filter their class definitions out
		// in ALL builds (read the IR predicate off the class's defaultType, not an FQN set).
		val classes = file.declarations.filterIsInstance<IrClass>().filter {
			it.kind == ClassKind.CLASS && userDefined(it) && it.name.asString() !in setOf("ClrRef", "StackBuffer", "Span") && !it.defaultType.isUnsignedArray()
		}
		// `object Foo { ... }` (non-companion) -> a singleton class with a static `INSTANCE` field; `IrGetObjectValue`
		// loads it. The shared-state-via-`object` case (feedback item 10). Companion/anonymous objects are handled
		// elsewhere; .NET-injected `object`s (Math, …) are static call sites, not user singletons.
		val objects = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.OBJECT && !it.isCompanion && userDefined(it) }
		// @ClrTypeAlias interfaces (Comparable/Iterable/Collection/List/…) are emitted as ordinary interfaces; bir2cir
		// drops them (no helper for a non-class kind). At use-sites BirTypeLowering substitutes them to the BCL interface.
		val interfaces = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.INTERFACE && userDefined(it) }
		val enums = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.ENUM_CLASS && userDefined(it) }
		val annClasses = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.ANNOTATION_CLASS && userDefined(it) }
		// Only USER properties (origin DEFINED) — a consuming module's FIR also holds plugin-INJECTED top-level props
		// (restored extension properties from a referenced DotKt assembly); those are the library's, not ours to emit.
		val topProps = file.declarations.filterIsInstance<IrProperty>().filter { it.origin.toString() == "DEFINED" }
		// A genuinely empty file emits nothing. (An "alias-only" file — e.g. String.kt / Primitives.kt / Comparable.kt —
		// is NOT empty: its @ClrTypeAlias type flows through `classes`/`interfaces` above and is emitted as an
		// ordinary type below, then dropped/hoisted by bir2cir's AliasHelperHoist. No special branch is needed.)
		if (functions.isEmpty() && classes.isEmpty() && objects.isEmpty() && interfaces.isEmpty() && enums.isEmpty() && annClasses.isEmpty() && topProps.isEmpty())
			return ""
		val className = fileClassName(file)
		fileClass = className
		// Entry point: top-level `fun main()` or `fun main(args: Array<String>)`.
		val hasMain = functions.any {
			it.name.asString() == "main" && run {
				val regs = it.parameters.filter { p -> p.kind == IrParameterKind.Regular }
				regs.isEmpty() || (regs.size == 1 && isArrayType(regs[0].type))
			}
		}
		// Top-level non-const `val`/`var` -> static fields of the file class (const is inlined by the frontend).
		val statFields = topProps.mapNotNull { p ->
			val bf = p.backingField ?: return@mapNotNull null
			if (p.isConst) return@mapNotNull null
			val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
			// A top-level `val` (or `var` with a non-public setter) -> mark the static field read-only so a downstream
			// consuming module (facadegen `tlprop ... ro`) restores it as `val`, rejecting external writes (#34b, mirrors
			// the member-field `readOnly` stamp).
			val ro = if (!p.isVar || (p.setter != null && visOf(p.setter!!) != "public")) ""","readOnly":true""" else ""
			"""{"name":${str(bf.name.asString())},"type":${birType(bf.type).toJson()},"static":true,"init":$init$ro${volatileFieldFlag(p)}}"""
		}
		// Super-typed companions (`companion object X : Base()`) -> lifted concrete singletons `<Outer>.InstanceClass`
		// (registered in anonNames so typeName resolves them consistently). Must run BEFORE any body emission so a
		// reference to the companion-as-value resolves to the lifted name everywhere.
		val superCompanions = (classes + objects + interfaces + enums + annClasses)
			.flatMap { listOf(it) + nestedClasses(it) + nestedObjects(it) }
			.mapNotNull { superTypedCompanion(it) }.distinct()
		superCompanions.forEach { c -> anonNames[c] = companionObjectTypeName(c) }
		// Emit functions and types first (this lifts lambdas into liftedMethods/liftedTypes), then append them.
		val fnMethods = functions.map { method(it, static = true) }
		// A top-level property with NO backing field (an EXTENSION property `val T.p`, or a computed `val p get() = …`)
		// -> emit its get_/set_<name> as STATIC methods (the receiver, if any, rides `__self`). A backing-field top-level
		// property stays a static field (above).
		val topPropAccessors = topProps.filter { it.backingField == null }.flatMap { p ->
			listOfNotNull(
				p.getter?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
				p.setter?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
		}
		// Basic enums -> real CLR enums (int-backed, for .NET interop); rich enums -> plain singleton classes.
		val (richEnums, basicEnums) = enums.partition { isRichEnum(it) }
		// Nested (non-inner) classes -> flattened to top-level synthetic types (they keep their real name, so
		// `new Nested(...)` and field ownerTypes resolve). `inner` classes need outer-`this` capture (deferred).
		val nestedParents = classes + interfaces + objects + annClasses
		val nested = nestedParents.flatMap { nestedClasses(it) }
		val nestedObjects = nestedParents.flatMap { nestedObjects(it) }
		val nestedEnums = nestedParents.flatMap { nestedEnums(it) }
		val (nestedRichEnums, nestedBasicEnums) = nestedEnums.partition { isRichEnum(it) }
		// `inner class`es flatten to top-level types that capture the enclosing instance (`__outer`).
		val inners = classes.flatMap { innerClasses(it) }
		// Nested interfaces (recursively, inside classes/interfaces/objects) -> real nested types so a `TimeSource.WithComparableMarks` supertype resolves.
		val nestedIfaces = nestedParents.flatMap { nestedInterfaces(it) }
		val typeDefs = (basicEnums + nestedBasicEnums).map { enumDef(it) } + (interfaces + nestedIfaces).map { interfaceDef(it) } +
			classes.map { typeDef(it) } + (objects + nestedObjects).map { typeDef(it, isObject = true) } + nested.map { typeDef(it) } + inners.map { innerClassDef(it) } +
			superCompanions.map { typeDef(it, isObject = true) } +
			(richEnums + nestedRichEnums).map { richEnumDef(it) } + annClasses.map { annotationDef(it) }
		val methods = (fnMethods + topPropAccessors + liftedMethods).joinToString(",")
		// #52 (kotc-purity): the CLR-representation synthetic TYPE definitions are no longer synthesized here — kotc emits
		// only the FACTS. bir2cir owns the type synthesis: SharedSyntheticSynthesis builds the fixed-shape
		// `dotkt$CharSequence` interface + `dotkt_KProperty(Impl)` from their use-site references; RefCellSynthesis
		// builds each `dotkt$Ref_<elem>` cell from the `refTypes` registry below; ClosureSynthesis builds each capturing
		// closure class from the `synthClass` fact on its `newClosure` node. The CLR-bound (@ClrTypeAlias) classes are
		// already in `typeDefs` (they flow through `classes` like any other type — kotc no longer strips them); bir2cir's
		// AliasHelperHoist drops each alias type def and, for a class, hoists its rule-3 members into the helper.
		val types = (typeDefs + liftedTypes).joinToString(",")
		return """{"fileClass":${str(className)},"hasMain":$hasMain,"fields":[${statFields.joinToString(",")}],"methods":[$methods],"types":[$types],"refTypes":[${refTypesJson()}]}"""
	}

	internal fun interfaceDef(iface: IrClass): String {
		fun ifaceMethod(fn: IrSimpleFunction, prop: IrProperty? = fn.correspondingPropertySymbol?.owner): String {
			// C3b reverse direction: a Kotlin interface extending a @Clr interface (Set : Collection->IReadOnlyCollection).
			// clrIfaceMemberName reads facadegen .NET-interop metadata ONLY (not @ClrIntrinsic); in the stdlib build
			// (CLR_TYPES_METADATA="") kotc emits the plain Kotlin get_size here for both ref and rt — the BCL override-slot
			// rename (get_size -> get_Count) is bir2cir's DeclarationRename off the ref.dll @ClrIntrinsic.
			val name = clrIfaceMemberName(fn) ?: (prop?.let { p -> (if (fn == p.getter) "get_" else "set_") + p.name.asString() } ?: fn.name.asString())
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
	internal fun enumDef(e: IrClass): String {
		val entries = e.declarations.filterIsInstance<IrEnumEntry>()
			.mapIndexed { i, ent -> """{"name":${str(ent.name.asString())},"ordinal":$i}""" }
		val nestedIn = emittedNestedParent(e)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null }
			?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
		return """{"name":${str(typeName(e))},"kind":"enum"$nestedIn,"entries":[${entries.joinToString(",")}]}"""
	}

	/** A "rich" enum has ctor params, user instance methods, or per-entry bodies -> can't be a CLR enum. */
	internal fun isRichEnum(ec: IrClass): Boolean {
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
	 * in the `.cctor`; `ToString`->`__name`; `values()`->fresh array; `valueOf(name)`->linear match.
	 */
	internal fun richEnumDef(ec: IrClass): String {
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
		// methods: concrete user methods + abstract member decls + ToString + values() + valueOf().
		val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
			.map { method(it, static = false) } +
			absMethods.map { m -> """{"name":${str(m.name.asString())},"static":false,"override":false,"virtual":true,"abstract":true,"vis":"public","params":[${paramsJsonList(m.parameters).joinToString(",")}],"ret":${birType(m.returnType).toJson()},"body":[]}""" }
		val sf = { e: IrEnumEntry -> """{"k":"staticField","ownerType":${fqnJson(name)},"name":${str(e.name.asString())}}""" }
		val toStr = """{"name":"ToString","static":false,"override":true,"virtual":true,"objectOverride":true,"vis":"public","params":[],"ret":${fqnJson("kotlin.String")},"body":[{"k":"return","value":{"k":"field","ownerType":${fqnJson(name)},"recv":{"k":"this"},"name":"__name"}}]}"""
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
	internal fun enumSuperArgs(cc: IrClass): List<String> {
		val ctor = cc.declarations.filterIsInstance<IrConstructor>().firstOrNull() ?: return emptyList()
		val call = (ctor.body as? IrBlockBody)?.statements?.firstNotNullOfOrNull { it as? IrEnumConstructorCall }
			?: return emptyList()
		return regularArgs(call).map { expr(it) }
	}

	/** A per-entry enum body `NAME(args) { override fun … }` -> a subclass `<>Enum_NAME : Enum` whose ctor takes only
	 *  (__name, __ordinal) and forwards them plus the baked-in `args` to the base ctor; carries the overriding methods. */
	internal fun enumEntrySubclass(subName: String, baseName: String, cc: IrClass, userArgs: List<String>): String {
		val overrides = cc.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.body != null && it.correspondingPropertySymbol == null }
			.joinToString(",") { method(it, static = false) }
		val baseArgs = (listOf("""{"k":"local","name":"__name"}""", """{"k":"local","name":"__ordinal"}""") + userArgs).joinToString(",")
		val subCtor = """{"params":[{"name":"__name","type":${fqnJson("kotlin.String")}},{"name":"__ordinal","type":${fqnJson("kotlin.Int")}}],"baseArgs":[$baseArgs],"thisArgs":null,"vis":"public","body":[]}"""
		return """{"name":${str(subName)},"kind":"class","abstract":false,"vis":"public","base":${fqnJson(baseName)},"interfaces":[],"fields":[],"ctors":[$subCtor],"methods":[$overrides]}"""
	}

	/** Nested non-inner user classes inside [c] (recursively); excludes companion/inner/anonymous/@Clr. */
	internal fun nestedClasses(c: IrClass): List<IrClass> {
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
	internal fun nestedObjects(c: IrClass): List<IrClass> {
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
	internal fun nestedEnums(c: IrClass): List<IrClass> {
		val out = ArrayList<IrClass>()
		c.declarations.filterIsInstance<IrClass>()
			.filter { clrName(it) == null && it.name.asString() != "<no name provided>" }
			.forEach { if (it.kind == ClassKind.ENUM_CLASS) out.add(it); out.addAll(nestedEnums(it)) }
		return out
	}

	/** Nested interfaces (recursively) inside a class OR interface (`TimeSource.WithComparableMarks`); emitted as real
	 *  nested types so a supertype reference to the bare name resolves. */
	internal fun nestedInterfaces(c: IrClass): List<IrClass> {
		val out = ArrayList<IrClass>()
		c.declarations.filterIsInstance<IrClass>()
			.filter { clrName(it) == null && it.name.asString() != "<no name provided>" }
			.forEach { if (it.kind == ClassKind.INTERFACE) out.add(it); out.addAll(nestedInterfaces(it)) }
		return out
	}

	/** `inner class`es nested (recursively) inside a class -> flattened to top-level synthetic types. */
	internal fun innerClasses(c: IrClass): List<IrClass> {
		val out = ArrayList<IrClass>()
		c.declarations.filterIsInstance<IrClass>()
			.filter { it.kind == ClassKind.CLASS && !it.isCompanion && clrName(it) == null && it.name.asString() != "<no name provided>" }
			.forEach { if (it.isInner) out.add(it); out.addAll(innerClasses(it)) }
		return out
	}

	/** Emit a flattened `inner class`: it captures the enclosing instance as a leading `__outer` ctor param/field. */
	/**
	 * The type parameters an `inner class` inherits from its enclosing class(es). A Kotlin `inner class` (e.g.
	 * `AbstractList<E>.IteratorImpl : Iterator<E>`) references the enclosing `E` but declares no own param. Reflection.Emit
	 * does NOT auto-inherit an enclosing type's generic params into a nested type, so emitting `IteratorImpl` with arity 0
	 * while its signatures reference the enclosing `E` (encoded as `VAR 0`) produces malformed metadata ("incorrect format",
	 * only caught at full-type-load batch validation). The Kotlin->CLR lowering is to RE-DECLARE the enclosing params on the
	 * inner class (own generic context) and reference it WITH those args — `IteratorImpl[gp:E]` — at every use site (the
	 * enclosing params are in scope wherever an inner class is referenced, since it captures the enclosing instance). This is
	 * a relationship-layer lowering (eventual home: bir2cir); it lives here for now alongside the other kotc-side
	 * lowerings (Unit->void, star-projection->object).
	 */
	/** True if [t] is or contains a `tv` (an unresolved type variable). Used by the lifted-anon capture scan to
	 *  decide whether an inline-substituted param resolves to an ENCLOSING generic param (must be captured) vs a
	 *  concrete type (resolves fine). */
	internal fun containsTv(t: TypeNode): Boolean = when (t) {
		is TypeNode.Tv -> true
		is TypeNode.Fqn -> t.args?.any { containsTv(it) } == true
		is TypeNode.Fn -> containsTv(t.ret) || t.params.any { containsTv(it) } || (t.recv?.let { containsTv(it) } == true)
		is TypeNode.Nullable -> containsTv(t.of)
		is TypeNode.Oblivious -> containsTv(t.of)   // frontend-only, but keep the match exhaustive
		is TypeNode.Array -> containsTv(t.elem)
		is TypeNode.ByRef -> containsTv(t.of)
	}

	internal fun innerEnclosingTypeParams(klass: IrClass): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
		if (!klass.isInner) return emptyList()
		val result = mutableListOf<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
		var p = klass.parent as? IrClass
		while (p != null) { result.addAll(0, p.typeParameters); p = if (p.isInner) p.parent as? IrClass else null }
		return result
	}

	internal fun innerClassDef(inner: IrClass): String {
		val outerThis = (inner.parent as? IrClass)?.thisReceiver
			?: return typeDef(inner)   // not actually inner-of-class; emit plainly
		captureSubst[outerThis] = """{"k":"field","ownerType":${fqnJson(typeName(inner))},"recv":{"k":"this"},"name":"__outer"}"""
		val def = typeDef(inner, listOf(outerThis to "__outer"))
		captureSubst.remove(outerThis)
		return def
	}

	/** `@ClrField` opt-out: emit this property as a plain (public) CLR FIELD, no accessor/property. Detected by short
	 *  name so any user-declared `ClrField` annotation triggers it. */
	internal fun isClrField(p: IrProperty): Boolean =
		p.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrField" }

	/** `@kotlin.concurrent.Volatile` on a `var`'s backing field: a pure Kotlin-language fact (like `suspend`/
	 *  `@Synchronized`, NOT a `@Clr*` binding). Emit a `"volatile":true` FIELD flag; bir2cir threads it through and
	 *  ilemit lowers it to a CLR volatile field (`modreq(IsVolatile)` + `volatile.` prefix — the C# `volatile` shape).
	 *  Matched by the field's OR the property's annotations (the FIELD-targeted annotation can land on either IR node). */
	internal fun isVolatile(p: IrProperty): Boolean {
		fun hasVol(anns: List<IrConstructorCall>) =
			anns.any { it.type.classFqName?.asString() == "kotlin.concurrent.Volatile" }
		return hasVol(p.annotations) || (p.backingField?.let { hasVol(it.annotations) } ?: false)
	}

	/** `,"volatile":true` field-flag fragment (empty when not volatile). */
	internal fun volatileFieldFlag(p: IrProperty): String = if (isVolatile(p)) ""","volatile":true""" else ""

	/** kotc's OWN synthetic `dotkt$CharSequence`: its `length` property getter is emitted as the `get_length` slot (an
	 *  override of the synthetic interface slot, NOT a user-written accessor). This is kotc's own type synthesis, not
	 *  facadegen .NET interop — so its name stays here. EVERY other override-slot name is now resolved by bir2cir's
	 *  DeclarationRename off the `overrides` marker + the refs: a facadegen-injected .NET interface/base member (owner
	 *  FQN -> a real .NET Type) and a stdlib @ClrTypeAlias/@ClrIntrinsic member (Collections.kt: size->get_Count,
	 *  get->get_Item, iterator->GetEnumerator; kotlin.AutoCloseable.close->Dispose) alike. kotc emits the PLAIN Kotlin
	 *  override name (+ its `get_`/`set_` accessor convention) and the `overrides` marker; the Kotlin<->CLR member
	 *  binding is bir2cir's (layer purity — no BCL slot name, no `@ClrIntrinsic`/injected-member-slot read here). */
	internal fun clrIfaceMemberName(fn: IrSimpleFunction): String? =
		(sequenceOf(fn) + fn.overriddenSymbols.asSequence().map { it.owner }).firstNotNullOfOrNull { owner ->
			if ((owner.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.CharSequence"
				&& owner.correspondingPropertySymbol?.owner?.name?.asString() == "length") "get_length" else null
		}

	/** STEP-1 (kotc->bir2cir clrName migration) — a PURE-KOTLIN override marker for an emitted member: the transitive
	 *  closure of interface/base members it overrides, each as {owner FQN, Kotlin member name, kind, arity}. NO CLR
	 *  knowledge (no @ClrIntrinsic read, no BCL name). bir2cir (Step 2) consumes this + the ref.dll @ClrIntrinsic to
	 *  derive the BCL slot name. Behavior-neutral: bir2cir strips
	 *  the `overrides` key, so it never reaches ilemit (Step 1 keeps CIR byte-identical). `member` is the property name
	 *  for an accessor (kind getter/setter) so bir2cir can resolve `get_`/`set_` + the property's @ClrIntrinsic. */
	internal fun overridesJson(fn: IrSimpleFunction): String {
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
	internal fun topLevelAccessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
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

	internal fun accessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
		val mname = clrIfaceMemberName(acc) ?: (if (isGetter) "get_" else "set_") + propName
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
		val clrIface = clrIfaceMemberName(acc) != null
		// An `override val/var` whose accessor overrides a base CLASS/ENUM_CLASS accessor must REUSE that base virtual
		// slot (`override`, not a fresh NewSlot) — EXACTLY like an overriding method (see method()'s `isOverride`).
		// Otherwise a concrete subclass leaves the base's abstract accessor slot unfilled -> TypeLoadException at load
		// ("get_X ... does not have an implementation"). This mirrors method() so property accessors and methods agree.
		// Interface members bind by name/signature (ilemit's DefineMethodOverride pass) so they don't need this flag;
		// use the accessor's OWN overriddenSymbols (a setter that ADDS to a base `val` has none -> stays a NewSlot).
		val isOverrideClass = acc.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
		val virtual = clrIface || acc.modality == Modality.OPEN || acc.modality == Modality.ABSTRACT || acc.overriddenSymbols.isNotEmpty()
		val vis = if (clrIface) "public" else visOf(acc)
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
		return """{"name":${str(mname)},"static":false,"override":${clrIface || isOverrideClass},"virtual":$virtual,"abstract":$isAbstract,"objectOverride":false,"vis":${str(vis)},"params":[$ps],"ret":${str(ret)},"body":[$body]$accAttrs${overridesJson(acc)}}"""
	}

	/** A user `annotation class Ann(val v: Int, …)` -> a plain BIR class carrying the pure-Kotlin `"annotation":true`
	 *  FLAG (ctor params -> public fields). "This is an annotation" is a Kotlin-language fact; "annotations extend
	 *  System.Attribute on the CLR" is the Kotlin<->CLR relation, so kotc emits ONLY the flag (base:null) and
	 *  bir2cir DERIVES `base = System.Attribute` from it (annotation-base-lowering-to-bir2cir, USER 2026-07-02).
	 *  kotc names NO CLR base type here. */
	internal fun annotationDef(klass: IrClass): String {
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
	internal fun attrsJson(anns: List<IrConstructorCall>): String {
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

	/** A property whose type is `kotlin.clr.ClrEvent<T>` — the compile-time-only fiction surfacing a .NET event.
	 *  A .NET event is subscribed via `+=`/`-=` and is NEVER a first-class value or a real inherited property, so
	 *  such a property must never be emitted as a member. This matters for a FAKE-OVERRIDE: when a Kotlin class
	 *  subclasses a .NET type whose interface carries an event (`class MyApp : Avalonia.Application`, whose bases
	 *  implement an event-bearing interface), fir2ir synthesizes a fake-override getter returning `ClrEvent<T>`;
	 *  declaring it would emit an accessor/property over the un-emittable `kotlin.clr.ClrEvent` type — skip it. */
	internal fun isClrEventProperty(p: IrProperty): Boolean =
		p.getter?.returnType?.classFqName?.asString() == "kotlin.clr.ClrEvent"

	internal fun typeDef(klass: IrClass, captures: List<Pair<IrValueDeclaration, String>> = emptyList(), isObject: Boolean = false, liftedAnon: Boolean = false, generated: Boolean = false): String {
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
		// Property accessors that override a .NET base virtual property -> emitted as get_/set_ override methods.
		// A `kotlin.clr.ClrEvent<T>` fake-override (a .NET event inherited via a base's interface) is NOT a real
		// property — skip it (see isClrEventProperty), else we emit an accessor over the un-emittable ClrEvent type.
		val clrAccessors = klass.declarations.filterIsInstance<IrProperty>()
			.filterNot { isClrEventProperty(it) }
			.flatMap { p -> listOfNotNull(clrAccessorMethod(p, p.getter), clrAccessorMethod(p, p.setter)) }
		// User custom accessors (`get() = …`/`set(v){…}`) -> get_/set_ methods (the access site routes through them).
		// A property optimizes to a plain field; but one implementing a KOTLIN INTERFACE property must emit a get_/set_
		// METHOD to bind the interface slot (property-accessor analog of the method-side overridesIface fix; e.g.
		// ComparableRange.start over ClosedRange.start). See design-clr-property-model.md.
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
			val getName = clrIfaceMemberName(p.getter!!) ?: "get_" + p.name.asString()
			val setName = if (emitsSet(p)) str(clrIfaceMemberName(p.setter!!) ?: "set_" + p.name.asString()) else "null"
			"""{"name":${str(p.name.asString())},"type":${birType(p.getter!!.returnType).toJson()},"get":${str(getName)},"set":$setName${overridesJson(p.getter!!)}}"""
		}
		val methods = (instMethods + statMethods + companionAccessors + clrAccessors + userAccessors).joinToString(",")
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
					else -> charSeqIface(st)?.let { fqnJson(it) } ?: stClass?.let { ownerSpec(it, st).toJson() }
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

	internal fun ctor(klass: IrClass, ctor: IrConstructor, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
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

	internal fun method(fn: IrSimpleFunction, static: Boolean): String {
		// An override of a CLASS or ENUM_CLASS member (the latter: a per-entry enum body overriding an abstract enum
		// member) reuses the base virtual slot. (Interface members bind by name/signature, handled elsewhere.)
		val isOverride = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
		// A method that implements/overrides a Kotlin INTERFACE member must be virtual on the CLR to bind the interface
		// slot — even when it is Kotlin-`final` (final override -> CLR `virtual final` = sealed). Otherwise the type
		// fails to load with "must be virtual to implement a method on an interface or super type" (e.g. Enum.compareTo,
		// the primitive Iterator.next).
		val overridesIface = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.INTERFACE }
		val isVirtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT || clrIfaceMemberName(fn) != null || overridesIface
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
			if (tc.isNotEmpty()) cfgFresh().also { tailrecCtx = TailrecCtx(tc, it, fn) } else null
		} else null
		val bodyStmts = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		tailrecCtx = savedTailrec
		val body = if (tailrecStart != null) """{"k":"label","id":$tailrecStart}${if (bodyStmts.isNotEmpty()) ",$bodyStmts" else ""}""" else bodyStmts
		refCellVars = savedRefCells
		if (extRecv != null) selfSubst.remove(extRecv)
		val selfParam = extRecv?.let { """{"name":"__self","type":${birType(it.type).toJson()}}""" }
		val ps = (listOfNotNull(selfParam) + paramsJsonList(fn.parameters, ownerFn = fn)).joinToString(",")
		// `override fun toString()/equals()/hashCode()` -> System.Object.ToString/Equals/GetHashCode so that
		// CLR virtual dispatch (Console.WriteLine, structural `==`) finds the override.
		val objName = objectMethodName(fn)
		val clrIfaceName = clrIfaceMemberName(fn)   // e.g. resumeWith -> ResumeWith when implementing Continuation<T>
		val emitName = clrIfaceName ?: objName ?: fn.name.asString()
		val isOvr = isOverride || objName != null || clrIfaceName != null
		// Object-overrides / interface members must stay public for virtual dispatch.
		// A PRIVATE TOP-LEVEL fun is FILE-private in Kotlin, but kotc's emission splits a file across CLR types
		// (the XKt file class + the file's classes), so CLR `private` under-approximates it: a same-file class
		// calling the helper threw MethodAccessException at run (Duration..cctor -> DurationKt.durationOfMillis).
		// Emit `internal` — the tightest CLR visibility that preserves same-file access (the same reasoning that
		// makes routed property backing fields internal). Class members keep their real visibility.
		val vis = if (objName != null || clrIfaceName != null) "public"
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
		return """{"name":${str(emitName)},"static":$static,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${objName != null},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}$mods${resultTypeJson(fn)},"params":[$ps],"ret":${birType(fn.returnType).toJson()},"body":[$body],"attrs":[${attrsJson(fn.annotations)}]${overridesJson(fn)}}"""
	}

	/** Structured declaration-modifier object (spec §2.1): a single `"mods":{name:true,…}` carrying ONLY the set flags
	 *  (absent key = not set), replacing the order-dependent `$kmods$inlineFlag$suspendField` fragment concatenation.
	 *  `inline` = the "inline body must travel" fact (isInlineWithLambda), the only inline shape ilemit splices. */
	internal fun funModsJson(fn: IrSimpleFunction, inline: Boolean = false): String {
		val flags = buildList {
			if (inline) add(""""inline":true""")
			if (fn.isInfix) add(""""infix":true""")
			if (fn.isOperator) add(""""operator":true""")
			if (fn.isSuspend) add(""""suspend":true""")
		}
		return if (flags.isEmpty()) "" else ""","mods":{${flags.joinToString(",")}}"""
	}

	/** A `suspend fun`'s Kotlin result type rides ALONGSIDE `mods.suspend` (it is a Type, not a modifier flag). */
	internal fun resultTypeJson(fn: IrSimpleFunction): String =
		if (fn.isSuspend) ""","suspendRet":${birType(fn.returnType).toJson()}""" else ""

	/** Structured class-modifier object (spec §2.1): `"mods":{name:true,…}` for class-nature Kotlin facts
	 *  (`fun`-interface, `sealed`, `annotation`, …) — only the set flags, absent = not set. */
	internal fun classModsJson(fnIface: Boolean = false, sealed: Boolean = false, annotation: Boolean = false): String {
		val flags = buildList {
			if (annotation) add(""""annotation":true""")
			if (fnIface) add(""""fun":true""")
			if (sealed) add(""""sealed":true""")
		}
		return if (flags.isEmpty()) "" else ""","mods":{${flags.joinToString(",")}}"""
	}

	/** An `inline fun` with at least one (inlinable) lambda parameter — the only inline shape whose body must travel
	 *  for cross-module consumption (lambda-less inline funs degrade to ordinary calls; the JIT inlines those). */
	internal fun isInlineWithLambda(fn: IrSimpleFunction): Boolean =
		fn.isInline && fn.parameters.any { it.kind == IrParameterKind.Regular && !it.isNoinline && birType(it.type) is TypeNode.Fn }

	// ===== Coroutine SUSPEND FACTS (kotc emits facts only; ALL coroutine lowering is bir2cir's) =====
	// kotc does NO coroutine lowering. A `suspend fun`/lambda body emits PLAINLY: decls carry `"suspend":true`
	// (+ `resultType`), suspend call sites carry `"suspendCall":true`, and a suspend lambda emits `newSuspendLambda`.
	// bir2cir consumes those facts to build the `ContinuationImpl` state machine + the public `Task<T>` bridge; kotc
	// bakes NO coroutine ABI. The helpers below (isAwaitIntrinsic / isSuspensionCall / containsSuspend) are the ONLY
	// coroutine code left in kotc, and they exist purely to DRIVE the fact emission (skip the await intrinsic method,
	// tag suspend calls).
	internal fun isAwaitIntrinsic(fn: IrSimpleFunction): Boolean =
		fn.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrAwait" }

	/** A suspension point: any call to a suspend function (the `.await()` intrinsic or a direct suspend call). */
	internal fun isSuspensionCall(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
		e is IrCall && e.symbol.owner.isSuspend

	internal fun containsSuspend(e: org.jetbrains.kotlin.ir.IrElement): Boolean {
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
	internal fun typeParamsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String {
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

	/**
	 * Owner-type spec for a member access / `new`: `Box[int]` when the receiver is a CONCRETE construction of a
	 * user generic, else the bare `Box`. Inside the generic type's own methods the receiver is `Box<T>` (args are
	 * the type's own parameters) -> bare name, so members resolve against the open FieldBuilder/MethodBuilder
	 * directly (the correct `!0`-typed reference), not a self-instantiation.
	 */
	internal fun ownerSpec(klass: IrClass?, recvType: IrType?): TypeNode {
		klass ?: return TypeNode.Fqn("?")
		// CharSequence (declaring class of a call on a CharSequence-typed value) -> the synthetic interface name.
		if (klass.fqNameWhenAvailable?.asString() == "kotlin.CharSequence") return TypeNode.Fqn("kotlin.CharSequence")
		val name = typeName(klass)
		// An `inner class` re-declares its enclosing type params; construct it WITH them (as `tv`). See innerEnclosingTypeParams.
		val enclArgs = innerEnclosingTypeParams(klass).map { tvOf(it) }
		if (klass.typeParameters.isEmpty())
			return if (enclArgs.isNotEmpty()) TypeNode.Fqn(name, enclArgs) else TypeNode.Fqn(name)
		// A type-parameter argument keeps its `tv` form (resolvable in the enclosing generic context), NOT the open type.
		// A `Unit` TYPE-ARG stays the real Unit identity; a STAR projection -> Any (mirroring birType).
		val args = (recvType as? IrSimpleType)?.arguments?.map { a ->
			val at = (a as? IrTypeProjection)?.type
			when {
				at == null -> OBJ
				at.isUnit() -> TypeNode.Fqn("kotlin.Unit")
				else -> birType(at)
			}
		}
		val all = enclArgs + (args ?: emptyList())
		return if (all.isEmpty()) TypeNode.Fqn(name) else TypeNode.Fqn(name, all)
	}

	internal fun paramsJson(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): String =
		paramsJsonList(params).joinToString(",")

	internal fun isValueParameter(p: IrValueParameter): Boolean =
		p.kind == IrParameterKind.Regular || p.kind == IrParameterKind.Context

	/**
	 * A property accessor that OVERRIDES a .NET base virtual property (e.g. `override val Message` over
	 * `System.Exception.Message`) -> a `get_<Name>`/`set_<Name>` method that reuses the base virtual slot
	 * (ilemit marks it Virtual + DefineMethodOverride against the .NET getter). Normal Kotlin properties stay
	 * field-modeled; only .NET-overriding accessors with a body need this. Returns null otherwise.
	 */
	internal fun clrAccessorMethod(prop: IrProperty, acc: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction?): String? {
		acc ?: return null
		if (acc.body == null) return null
		// The accessor overrides a member whose (real) declaring type is a .NET type.
		// Only a .NET base CLASS virtual property uses clrOverride. A @Clr INTERFACE property (Collection.size) goes via
		// userAccessors + clrIfaceMemberName (-> get_Count), binding the interface slot rather than a generic clrOverride.
		val clrOwnerClass = acc.overriddenSymbols.asSequence()
			.map { it.owner }.mapNotNull { (if (it.isFakeOverride) it.resolveFakeOverride() else it)?.parent as? IrClass }
			.firstOrNull { clrName(it) != null } ?: return null
		if (clrOwnerClass.kind == ClassKind.INTERFACE) return null
		val clrOwner = clrName(clrOwnerClass)!!
		val isGetter = acc == prop.getter
		// The accessor slot is `get_`/`set_` + the property's Kotlin name — which, for a facadegen-injected .NET base,
		// IS the .NET property name (facadegen injects the member under its .NET identity). kotc no longer reads a member
		// slot name (that resolution is bir2cir's); the type-origin (`clrOwnerClass`) resolution above stays kotc's.
		val emitName = (if (isGetter) "get_" else "set_") + prop.name.asString()
		val body = (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		val ps = acc.parameters.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
		val ret = if (isGetter) birType(acc.returnType) else TypeNode.Fqn("kotlin.Unit")
		return """{"name":${str(emitName)},"static":false,"override":true,"virtual":true,"objectOverride":false,"clrOverride":${str(clrOwner)},"vis":"public","params":[$ps],"ret":${str(ret)},"body":[$body]}"""
	}

	/** THE 2-TIER default-argument test (docs/dotkt-semantics.md): can the parameter's OWN CLR type carry its default as
	 *  a `[DefaultParameterValue]` constant? TRUE (Tier 1) — a primitive/char/bool const on its primitive param, a String
	 *  const on a `String` param, or a null const on any nullable/reference param → native `[Optional]`+`[DefaultParameterValue]`.
	 *  FALSE (Tier 2) — a String const on a NON-String reference param (`CharSequence`: a string constant cannot sit on an
	 *  interface-typed param), or ANY non-constant default → `@KotlinDefault(bir)` + a REQUIRED param (a kcc consumer
	 *  splices the expression, a C# consumer passes the arg explicitly). */
	internal fun isMetadataRepresentableDefault(p: org.jetbrains.kotlin.ir.declarations.IrValueParameter): Boolean {
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
	internal fun carriesKotlinDefault(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
		!fn.isSuspend && fn.parameters.none { it.kind == IrParameterKind.DispatchReceiver } &&
			fn.parameters.any { it.kind == IrParameterKind.Regular && it.defaultValue != null }

	/** A data-class `copy` synthetic — `copy` cannot be user-declared on a data class, so name + `isData` parent is exact. */
	internal fun isDataClassCopy(fn: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction): Boolean =
		fn.name.asString() == "copy" && (fn.parent as? IrClass)?.isData == true

	internal fun paramsJsonList(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>,
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
	internal fun overloadSigField(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String {
		val ext = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }?.let { birType(it.type) }
		val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { birType(it.type) }
		return ""","sig":[${(listOfNotNull(ext) + regs).joinToString(",") { it.toJson() }}]"""
	}


	/** A loop label (Kotlin `outer@`) as JSON, or null. break/continue target loops by this label. */
	internal fun labelJson(label: String?): String = label?.let { str(it) } ?: "null"

	/** A loop body: a block's statements, or a single bare statement (single-statement loop bodies). */
	internal fun loopBody(body: IrExpression?): String = when (body) {
		null -> ""
		is IrBlock -> body.statements.joinToString(",") { stmt(it) }
		else -> stmt(body)
	}

	// Active CFG loops: (loop, continueLabelId, breakLabelId). A break/continue is matched to its target by
	// loop reference identity (so `break@outer` resolves), then emitted as `goto` the right label.
	internal val cfgLoopStack = ArrayList<Triple<org.jetbrains.kotlin.ir.expressions.IrLoop, Int, Int>>()

	/** Wrap a statement-position control transfer ([xfer] = a `goto`/`break`/`continue` node) so it can sit in an
	 *  EXPRESSION slot (a `break`/`continue` used as an `if`/`when` branch value). The transfer runs first and jumps
	 *  away; the `throw null` result is unreachable dead code that gives the valueBlock a well-formed result which
	 *  never falls through to the surrounding merge — so the merge keeps only the live branch's type. */
	internal fun breakContinueExpr(xfer: String): String =
		"""{"k":"valueBlock","stmts":[$xfer],"result":{"k":"throwExpr","value":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}}}"""

	/** Active `tailrec` self-tail-call rewrite for the function currently being emitted. `calls` = the set of
	 *  self-calls the frontend validated as tail-recursive (identity-keyed); `startLabel` = the CFG label at the
	 *  method's entry that a tail call jumps back to (see [tailrecJump]); `fn` = the function whose params are the
	 *  loop variables. Null unless inside a `tailrec` fn body that actually has a tail self-call. */
	internal class TailrecCtx(val calls: Set<IrCall>, val startLabel: Int, val fn: IrSimpleFunction)
	internal var tailrecCtx: TailrecCtx? = null

	/** The standard tail-call optimization: a self-tail-call in a `tailrec` fn becomes a back-jump to the method's
	 *  entry after reassigning the parameters to the call's arguments (Kotlin/JVM's own `tailrec` lowering, which our
	 *  pipeline skips because it runs Fir2Ir straight into our backend, no JVM lowerings — so without this deep tail
	 *  recursion overflows the CLR stack; §2b). The call sits in an EXPRESSION slot (`return f(...)`, or a `when`/`if`
	 *  branch feeding the return), so we emit a `valueBlock`: evaluate every argument into a temp FIRST (so a later arg
	 *  reading an earlier param — `f(n-1, acc+n)` — is not corrupted by the reassignment), reassign each param (a
	 *  `setLocal` on a param name emits `starg`), then `goto` the entry label. The block's result is an unreachable
	 *  `throwExpr` (the jump already left), mirroring [breakContinueExpr] — the surrounding `return` never executes. */
	internal fun tailrecJump(call: IrCall, ctx: TailrecCtx): String {
		data class Reassign(val name: String, val tmp: String, val valueJson: String, val type: org.jetbrains.kotlin.ir.types.IrType)
		val reassigns = ArrayList<Reassign>()
		ctx.fn.parameters.forEachIndexed { i, p ->
			// The dispatch receiver of a member `tailrec` self-call is the SAME `this` (Kotlin requires it) — never reassigned.
			if (p.kind == IrParameterKind.DispatchReceiver) return@forEachIndexed
			val arg = call.arguments.getOrNull(i) ?: return@forEachIndexed
			val name = if (p.kind == IrParameterKind.ExtensionReceiver) "__self" else p.name.asString()
			reassigns.add(Reassign(name, "__tailrec_${ctx.startLabel}_$i", coerceValue(arg, p.type), p.type))
		}
		val stmts = ArrayList<String>()
		reassigns.forEach { stmts.add("""{"k":"var","name":${str(it.tmp)},"type":${birType(it.type).toJson()},"init":${it.valueJson}}""") }
		reassigns.forEach { stmts.add("""{"k":"setLocal","name":${str(it.name)},"value":{"k":"local","name":${str(it.tmp)}}}""") }
		stmts.add("""{"k":"goto","id":${ctx.startLabel}}""")
		return """{"k":"valueBlock","stmts":[${stmts.joinToString(",")}],"result":{"k":"throwExpr","value":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}}}"""
	}

	/** `while(c){B}` -> CFG block: `START: if(!c) goto END; B; goto START; END:`. continue->START, break->END. */
	internal fun cfgWhile(node: IrWhileLoop): String {
		val start = cfgFresh(); val end = cfgFresh()
		cfgLoopStack.add(Triple(node, start, end))
		val body = loopBody(node.body)
		cfgLoopStack.removeAt(cfgLoopStack.size - 1)
		val parts = ArrayList<String>()
		parts.add("""{"k":"label","id":$start}""")
		parts.add("""{"k":"brIf","id":$end,"on":false,"cond":${expr(node.condition)}}""")
		body.takeIf { it.isNotEmpty() }?.let { parts.add(it) }
		parts.add("""{"k":"goto","id":$start}""")
		parts.add("""{"k":"label","id":$end}""")
		return """{"k":"block","body":[${parts.joinToString(",")}]}"""
	}

	/** `do{B}while(c)` -> CFG: `START: B; CONT: if(c) goto START; END:`. continue->CONT, break->END. */
	internal fun cfgDoWhile(node: IrDoWhileLoop): String {
		val start = cfgFresh(); val cont = cfgFresh(); val end = cfgFresh()
		cfgLoopStack.add(Triple(node, cont, end))
		val body = loopBody(node.body)
		cfgLoopStack.removeAt(cfgLoopStack.size - 1)
		val parts = ArrayList<String>()
		parts.add("""{"k":"label","id":$start}""")
		body.takeIf { it.isNotEmpty() }?.let { parts.add(it) }
		parts.add("""{"k":"label","id":$cont}""")
		parts.add("""{"k":"brIf","id":$start,"on":true,"cond":${expr(node.condition)}}""")
		parts.add("""{"k":"label","id":$end}""")
		return """{"k":"block","body":[${parts.joinToString(",")}]}"""
	}

	/** A Kotlin `for (x in a..b / array)` -> a BIR counter loop / indexed array loop, or null. */
	internal fun birForLoop(block: IrBlock): String? {
		val iterVar = block.statements.getOrNull(0) as? IrVariable
		val whileLoop = block.statements.getOrNull(1) as? IrWhileLoop
		val bodyBlock = whileLoop?.body as? IrBlock
		val loopVar = bodyBlock?.statements?.getOrNull(0) as? IrVariable
		if (iterVar == null || bodyBlock == null || loopVar == null) return null
		val source = (iterVar.initializer as? IrCall)?.let { dispatchReceiver(it) ?: extensionReceiver(it) }
		val body = bodyBlock.statements.drop(1).joinToString(",") { stmt(it) }
		val lbl = labelJson(whileLoop.label)
		// `for (x in array)` -> an indexed loop (avoids the kotlin iterator types). No `elem`: bir2cir derives the
		// loop-variable element type off the array operand's (now faithful) type.
		if (source != null && isArrayType(source.type))
			return """{"k":"forArray","label":$lbl,"var":${str(loopVar.name.asString())},"array":${expr(source)},"body":[$body]}"""
		// NON-array for-loops: kotc no longer classifies the source as a counted RANGE, an `a downTo b` counter, or a
		// stdlib collection — those decisions each need a `kotlin.ranges.*`/`kotlin.collections.*` FQN or the `downTo`
		// operator identity resolved against the stdlib, a Kotlin<->CLR relation that belongs in bir2cir. kotc emits
		// the FAITHFUL source + its runtime type token (`srcType`) + the element type (`elem`, a pure Kotlin loop-var
		// fact) and lets bir2cir's ForInLowering dispatch: a counted range (IntRange, or IntProgression in a stdlib
		// self-build) -> `forRange`; an `a downTo b` in a consumer build -> a counted `for`; a .NET/Sequence
		// enumerable, or a stdlib collection in a stdlib self-build -> `forEachInline`; anything else -> the `fallback`
		// (the FIR-desugared iterator-protocol block emitted below). NO `kotlin.ranges`/`kotlin.collections` FQN or
		// `downTo` classification leaves kotc.
		//
		// `elem` = the source's first type arg, else the loop var's type — the SAME value the forEachInline path needs;
		// bir2cir reads it verbatim when it turns a stdlib-collection `forIn` into `forEachInline`.
		val elem = (source?.type as? IrSimpleType)?.arguments?.firstOrNull()
			?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: birType(loopVar.type)
		// A .NET IEnumerable<T> (@Clr type) or a kotlin.sequences.Sequence -> enumerate via GetEnumerator
		// (forEachInline). `srcType` rides along so bir2cir can redirect a stdlib-build range (IntRange/IntProgression,
		// which IS stdlib-iterable) to forRange; a non-range enumerable keeps forEachInline (bir2cir strips srcType).
		val forInEnumerable = source != null && ((source.type.classifierOrNull?.owner as? IrClass)?.let { clrName(it) } != null
			|| source.type.classFqName?.asString() == "kotlin.sequences.Sequence")
		if (source != null && forInEnumerable) {
			return """{"k":"forEachInline","label":$lbl,"elem":${str(elem)},"src":${expr(source)},"srcType":${birType(source.type).toJson()},"var":${str(loopVar.name.asString())},"body":[$body]}"""
		}
		// Any other recovered source (a range VALUE like `indices`/a stored `IntRange`, a `rangeTo`/`until`/`downTo`
		// operator, or a plain kotlin.* collection) -> a faithful `forIn` carrying the source + its type token + the
		// element type + the loop body, plus the `fallback` = the FIR-desugared iterator-protocol block (what kotc used
		// to emit by returning null here). bir2cir's ForInLowering picks forRange (counted range), a counted `for`
		// (consumer-build downTo), forEachInline (stdlib-build collection), or the fallback (everything else).
		if (source != null)
			return """{"k":"forIn","label":$lbl,"elem":${str(elem)},"src":${expr(source)},"srcType":${birType(source.type).toJson()},"var":${str(loopVar.name.asString())},"body":[$body],"fallback":{"k":"block","body":[${block.statements.joinToString(",") { stmt(it) }}]}}"""
		return null
	}

	internal fun tryStmt(node: IrTry): String {
		val catches = node.catches.joinToString(",") { c ->
			val p = c.catchParameter
			// Use birType so a USER exception class catches as its own type (`@AppErr`), not `object`
			// (which degrades to System.Object — an unverifiable catch).
			"""{"excType":${birType(p.type).toJson()},"var":${str(p.name.asString())},"body":[${bodyStmts(c.result)}]}"""
		}
		val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
		return """{"k":"try","type":${birType(node.type).toJson()},"body":[${bodyStmts(node.tryResult)}],"catches":[$catches]$finally}"""
	}

	internal fun bodyStmts(e: IrExpression): String =
		if (e is IrBlock) e.statements.joinToString(",") { stmt(it) } else stmt(e)

	/** `try`/`catch` in value position -> a temp local assigned in each branch, returned via a valueBlock. */
	internal fun tryExpr(node: IrTry): String {
		val tv = "dotkt\$tryval${scopeCounter++}"
		val tryBody = bodyStmtsAssign(node.tryResult, tv)
		val catches = node.catches.joinToString(",") { c ->
			val p = c.catchParameter
			// birType (matching tryStmt) so the catch type stays the Kotlin FQN that bir2cir lowers via @ClrTypeAlias —
			// a USER exception class catches as its own `@AppErr`, a stdlib one as its BCL alias.
			"""{"excType":${birType(p.type).toJson()},"var":${str(p.name.asString())},"body":[${bodyStmtsAssign(c.result, tv)}]}"""
		}
		val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
		val tryS = """{"k":"try","type":${fqnJson("kotlin.Unit")},"body":[$tryBody],"catches":[$catches]$finally}"""
		return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(tv)},"type":${birType(node.type).toJson()}},$tryS],"result":{"k":"local","name":${str(tv)}}}"""
	}

	/** Like [bodyStmts], but the branch's final value-expression is assigned to `tv` (a value already throws/returns
	 *  -> emitted as-is). For try-as-expression: each branch leaves its result in the temp. */
	internal fun bodyStmtsAssign(e: IrExpression, tv: String): String {
		val stmts = if (e is IrBlock) e.statements else listOf(e)
		val pre = stmts.dropLast(1).joinToString(",") { stmt(it) }
		val last = stmts.lastOrNull()
		val tail = when {
			last is IrExpression && !last.type.isUnit() && last.type.classFqName?.asString() != "kotlin.Nothing" ->
				"""{"k":"setLocal","name":${str(tv)},"value":${expr(last)}}"""
			last != null -> stmt(last)
			else -> ""
		}
		return listOf(pre, tail).filter { it.isNotEmpty() }.joinToString(",")
	}

	/**
	 * Statement-position `if`/`when` -> CFG branches (E-0.5 §5.3): each non-else branch is
	 * `brIf NEXT (!cond); body; goto END; NEXT:`; the else body falls through to `END:`. Expression-position
	 * if/when keep the value form (`ternary`, via expr). Mixes freely with CFG loops (break/return inside work).
	 */
	internal fun cfgWhen(node: IrWhen): String {
		val end = cfgFresh()
		val parts = ArrayList<String>()
		for (br in node.branches) {
			val isElse = (br.condition as? IrConst)?.value == true
			if (isElse) {
				stmt(br.result).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
			} else {
				val next = cfgFresh()
				parts.add("""{"k":"brIf","id":$next,"on":false,"cond":${expr(br.condition)}}""")
				stmt(br.result).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
				parts.add("""{"k":"goto","id":$end}""")
				parts.add("""{"k":"label","id":$next}""")
			}
		}
		parts.add("""{"k":"label","id":$end}""")
		return """{"k":"block","body":[${parts.joinToString(",")}]}"""
	}



	/**
	 * A lambda -> a delegate. Non-capturing lambdas lift to a static method (`newDelegate`); capturing
	 * lambdas synthesize a closure class (fields = captured vars, instance `invoke` method) (`newClosure`).
	 */
	/**
	 * The enclosing type parameters a synthesized closure CLASS must be generic over: those referenced by its capture
	 * field types (and its own parameter/return types). On the CLR generics are reified, so a closure that captures a
	 * `T`-typed value (or a `List<T>` / `(T)->Unit`) becomes a SEPARATE class with a `gp:T` field — and `T` (an
	 * enclosing *method* type parameter) is not in scope from inside that class. The closure class must therefore
	 * declare `T` itself and be instantiated with the enclosing `T` at `newClosure`, or `MapType` fails to resolve it.
	 */
	private fun freeTypeParams(types: List<IrType>): List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter> {
		val acc = LinkedHashSet<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>()
		fun walk(t: IrType) {
			(t.classifierOrNull as? IrTypeParameterSymbol)?.let { acc.add(it.owner) }
			if (t is IrSimpleType) t.arguments.forEach { (it as? IrTypeProjection)?.type?.let(::walk) }
		}
		types.forEach(::walk)
		return acc.toList()
	}

	/** Type operands USED in a function body (e.g. `x is R` / `x as R` / `R::class`). A lifted closure must be generic
	 *  over these too: on the CLR generics are reified, so `is R` works once the lifted method carries `R` — unlike the
	 *  JVM, which needs `reified`+inlining. freeTypeParams over (params+return+captures) alone misses a body-only `R`. */
	private fun bodyTypeOperands(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrType> {
		val out = ArrayList<IrType>()
		fn.body?.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				// Don't descend into NESTED lambdas/local funs — they compute their own free type params when lifted.
				if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
				when (element) {
					is IrTypeOperatorCall -> out.add(element.typeOperand)
					is IrClassReference -> out.add(element.classType)
					else -> {}
				}
				element.acceptChildrenVoid(this)
			}
		})
		return out
	}

	/**
	 * A `suspend` lambda literal -> the `newSuspendLambda` BIR node (the dormant bir2cir SuspendLambdaLowering consumer).
	 * Emits ONLY pure Kotlin facts — captures, own params, result type, enclosing type-param names, and the body EXACTLY
	 * as a suspend-fun body (its suspend calls already carry `"suspendCall":true`). bir2cir builds the `ContinuationImpl`
	 * state machine (create/invokeSuspend/resume) from these; kotc bakes no coroutine ABI. Emits the pure facts for
	 * ANY arity N — bir2cir's SuspendLambda create() protocol covers 0/1 (fixed create() slots) and >= 2 (the general
	 * create(args, completion) slot); kotc no longer gates on arity.
	 * Restricted-suspension builder lambdas (`@RestrictsSuspension` on the extension-receiver scope, e.g.
	 * `sequence { }`/`iterator { }`'s `SequenceScope`) flow through THIS path too — bir2cir picks the
	 * `RestrictedSuspendLambda` base from the scope's annotation. kotc has no `sequence`/`yield` knowledge.
	 * Captures/params reuse the SAME machinery as the closure path (`capturedVars(includeThis=true)` / `captureFieldName`
	 * / `captureFieldType`). NOTE: unlike newClosure, the body is emitted WITHOUT installing `captureSubst` — bir2cir's
	 * SM builder rewrites captured-var reads (plain `{"k":"local"}`) into SM field reads itself. typeArgs are the BARE
	 * enclosing type-param names (bir2cir prepends `gp:` when it instantiates the open SM), NOT the `gp:`-prefixed form
	 * newClosure emits for ilemit.
	 */
	private fun suspendLambda(node: IrFunctionExpression): String? {
		val fn = node.function
		// Own params in delegate order (extension receiver first, then regular) — matches lambdaParamsJson + bir2cir's
		// create() views (arity 0/1 = fixed create() slots, arity >= 2 = create(args, completion)). arity = the count.
		val ownParams = orderedLambdaParams(fn)
		// Restricted-suspension builders (`sequence { }`/`iterator { }`'s @RestrictsSuspension SequenceScope receiver)
		// now flow through this SAME suspend-lambda path: bir2cir gives the lambda the `RestrictedSuspendLambda` base
		// (not the plain SuspendLambda), so the cold-core builder runs. No exclusion here — kotc emits the pure suspend
		// facts and bir2cir picks the restricted base from the receiver scope's @RestrictsSuspension annotation.
		val captures = capturedVars(fn, includeThis = true)
		val capturesJson = captures.joinToString(",") { d ->
			"""{"name":${str(captureFieldName(d))},"type":${str(captureFieldType(d))}}"""
		}
		val paramsJson = lambdaParamsJson(ownParams)
		val resultType = birType(fn.returnType)
		// Enclosing generic type params referenced by the SM (captures/params/return/body operands) -> open SM
		// instantiation. BARE names: bir2cir prepends `gp:`.
		val freeTps = freeTypeParams(captures.map { it.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		// The SM's own generic-parameter NAME declarations (the enclosing free type params the state machine is
		// generic over) — a type-param DECLARATION list (bir2cir names the SM's params + instantiates `!i`), NOT a
		// type-USAGE slot, so it rides as the `typeParams` name shorthand (§2.5), consistent with the other lambda paths.
		val typeParamsBare = freeTps.joinToString(",") { str(it.name.asString()) }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		return """{"k":"newSuspendLambda","arity":${ownParams.size},"captures":[$capturesJson],"params":[$paramsJson],"suspendRet":${str(resultType)},"typeParams":[$typeParamsBare],"body":[$body],"funcType":${funcTypeOf(fn).toJson()}}"""
	}

	internal fun lambda(node: IrFunctionExpression): String {
		val fn = node.function
		// A `suspend` lambda LITERAL -> a `newSuspendLambda` node: bir2cir turns it into a SuspendLambda state machine
		// (app-build only; the SM's create/resume protocol makes `blockOn { ... }` run). kotc emits only the pure FACTS
		// (captures/params/body-with-suspendCall-tags); the SM lowering is downstream. Any arity N flows through
		// suspendLambda now; restricted-suspension builders (sequence{}/iterator{}) go through it too — bir2cir gives
		// them the RestrictedSuspendLambda base.
		if (fn.isSuspend) suspendLambda(node)?.let { return it }
		// kotc does NO coroutine lowering: a `suspend () -> T` lambda emits as a PLAIN lambda (its suspend calls carry
		// `"suspendCall":true`); the Task-ABI / state-machine lowering is a deferred downstream layer. So the declared
		// return / delegate type stay the plain Kotlin shapes here.
		val ret = birType(fn.returnType)
		val ftype = funcTypeOf(fn)
		// A lambda has no `this` of its own, so a referenced `<this>` is the enclosing instance -> capture it.
		val captures = capturedVars(fn, includeThis = true)
		if (captures.isEmpty()) {
			val lname = "__lambda${lambdaCounter++}"
			val freeTps = freeTypeParams(fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
			val typeParams = typeParamsJson(freeTps)
			run {
				val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false$typeParams,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}""")
			}
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${str(ftype)}$typeArgs}"""
		}
		// Capturing: build a closure class. Captures rewrite to `this.<field>` (by symbol identity, so the
		// enclosing `this` — captured when the lambda reads a member — maps to a `__outer` field, not the
		// closure's own `this`). For a CPS suspend lambda the closure `invoke` is an INSTANCE coroutine; ilemit
		// captures the closure `this` into the state machine so resume can still read the captured-var fields.
		val cname = "dotkt\$${synthScope}\$Closure${closureCounter++}"
		val capPairs = captures.map { it to captureFieldName(it) }
		// Save any prior substitution for each captured decl so the OUTER binding (e.g. an intrinsic block's `c`
		// bound to the coroutine's own continuation) is restored after the body — not blown away — so the capture
		// VALUE (capValueExpr below) is still evaluated correctly in the enclosing context.
		val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
		capPairs.forEach { (decl, fname) ->
			captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		}
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
		val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		// The closure must be GENERIC over any enclosing type parameters it captures (reified CLR generics — a `gp:T`
		// field is unresolved otherwise). Declare them on the class and pass them as type arguments at `newClosure`.
		val freeTps = freeTypeParams(capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		// #52 (kotc-purity): kotc no longer SYNTHESIZES the closure CLASS (a CLR-representation type). It emits the raw
		// build-INGREDIENTS as a transient `synthClass` fact (capture fields=name+type, invoke params/ret/body, generic
		// type-param decls); bir2cir's ClosureSynthesis assembles the actual closure class (kind/base/interfaces wrapper +
		// the ctor field-init body) into the file `types`, then STRIPS `synthClass`, leaving the lean `newClosure`
		// (closureType + capture VALUE exprs + funcType + typeArgs) that ilemit consumes for the `new` — byte-identical.
		val synthClass = """{"name":${str(cname)},"fields":[$fields],"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]${typeParamsJson(freeTps)}}"""
		// Capture values are evaluated in the enclosing context (the outer `this`, or an outer local).
		val capExprs = captures.joinToString(",") { capValueExpr(it) }
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		return """{"k":"newClosure","closureType":${fqnJson(cname)},"captures":[$capExprs],"method":"invoke","funcType":${str(ftype)}$typeArgs,"synthClass":$synthClass}"""
	}

	/**
	 * SAM conversion `Comparator { a, b -> … }` -> a synthetic class that IMPLEMENTS the fun interface (the SAM method =
	 * the lambda body) and is instantiated via `newSam`. Unlike a function-type lambda (which lowers to a Func delegate),
	 * a fun-interface value is used by INTERFACE (`comparator.compare(...)`), so a delegate has no matching method
	 * (EntryPointNotFound). This mirrors the closure-class build but implements the iface + names the method after the SAM
	 * + override:true, and returns the instance itself (not a delegate). Reuses the working object:Comparator emission.
	 */
	internal fun samConversion(node: IrTypeOperatorCall): String {
		val funIface = node.typeOperand
		val ifaceClass = funIface.classifierOrNull?.owner as? IrClass ?: return expr(node.argument)
		val lamExpr = node.argument as? IrFunctionExpression ?: return expr(node.argument)   // fun-ref / existing impl -> fall back
		val fn = lamExpr.function
		val sam = ifaceClass.declarations.filterIsInstance<IrSimpleFunction>()
			.singleOrNull { it.modality == org.jetbrains.kotlin.descriptors.Modality.ABSTRACT } ?: return expr(node.argument)
		val samName = clrIfaceMemberName(sam) ?: sam.name.asString()
		val ret = birType(fn.returnType)
		val captures = capturedVars(fn, includeThis = true)
		val cname = "dotkt\$${synthScope}\$Sam${closureCounter++}"
		val capPairs = captures.map { it to captureFieldName(it) }
		// (kotc reads NEITHER @ClrTypeAlias NOR @ClrIntrinsic — foundational invariant.) The stdlib no longer aliases any
		// `fun interface` to a NON-generic BCL interface (Comparator is a plain Kotlin fun interface), so there is no
		// object-param erasure / SAM-arg cast bridge to apply here; the SAM shim implements the Kotlin fun-interface
		// identity directly and bir2cir derives any CLR type off the ref.dll.
		val savedSubst = java.util.IdentityHashMap<IrValueDeclaration, String?>()
		capPairs.forEach { (decl, fname) -> savedSubst[decl] = captureSubst[decl]; captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}""" }
		val samParams = lambdaParamsJson(fn.parameters)
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		val samMethod = """{"name":${str(samName)},"static":false,"override":true,"virtual":true,"params":[$samParams],"ret":${str(ret)},"body":[$body]}"""
		savedSubst.forEach { (decl, prev) -> if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
		val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		val ctorBody = capPairs.joinToString(",") { (_, fname) -> """{"k":"setField","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}""" }
		val ifaceSpec = ownerSpec(ifaceClass, funIface) ?: birType(funIface)
		val freeTps = freeTypeParams(listOf(funIface) + capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType) + bodyTypeOperands(fn))
		liftedTypes.add("""{"name":${str(cname)},"kind":"class","generated":true${typeParamsJson(freeTps)},"base":null,"interfaces":[${str(ifaceSpec)}],"fields":[$fields],"ctors":[{"params":[$fields],"baseArgs":null,"body":[$ctorBody]}],"methods":[$samMethod]}""")
		val capExprs = captures.joinToString(",") { capValueExpr(it) }
		val tArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
		return """{"k":"newSam","samType":${fqnJson(cname)},"captures":[$capExprs]$tArgs}"""
	}

	/**
	 * A callable reference `::foo` -> a delegate bound to the referenced function. v1 scope: a top-level/static
	 * function reference (no receiver, no bound args) reuses the lambda `newDelegate` path — top-level funs are
	 * emitted as static file-class methods, so `FindStatic(name)` resolves the `ldftn` target. Bound-instance
	 * (`obj::method`), member, and constructor references are deferred (clean `unsupportedExpr`).
	 */
	internal fun functionRef(node: IrFunctionReference): String {
		// `::Ctor` (constructor reference) -> a lifted static factory `__ctorref_N(args) = new T(args)`, bound as a
		// delegate (delegates can't bind a ctor directly). `Func<…,UserType>` now resolves via DelegateCtor.
		(node.symbol.owner as? IrConstructor)?.let { ctor ->
			val klass = ctor.parent as? IrClass
			if (klass != null && clrName(klass) == null) {
				val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
				val lname = "__ctorref${lambdaCounter++}"
				val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
				val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retT = birType(ctor.returnType)
				val newE = """{"k":"new","type":${ownerSpec(klass, ctor.returnType).toJson()},"args":[$argsJson]}"""
				val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
				return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
			}
			// `::NetType` — a lifted factory `__ctorref(args) = new NetType(args)` (newClr), bound as a delegate.
			if (klass != null) {
				val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
				val lname = "__ctorref${lambdaCounter++}"
				val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }
				val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retT = birType(ctor.returnType)
				val newE = """{"k":"newClr","type":${fqnJson(clrName(klass)!!)},"argTypes":[${ps.joinToString(",") { birType(it.type).toJson() }}],"args":[$argsJson]}"""
				val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
				return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
			}
			return unsupported(node, "this constructor reference", "the constructor's class could not be resolved")
		}
		val fn = node.symbol.owner as? IrSimpleFunction
			?: return unsupported(node, "this function reference", "only references to plain (simple) functions are supported")
		val dispatchIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
		// `::topLevelFun` — no receiver: a delegate over the static file-class method (FindStatic resolves it).
		if (dispatchIdx < 0 && !hasExt)
			return """{"k":"newDelegate","method":${str(fn.name.asString())},"funcType":${funcTypeOf(fn).toJson()}}"""
		// `obj::method` — a bound instance reference: a delegate whose target is the bound receiver. Only USER
		// classes (the method resolves via FindMethod); .NET-method / extension / unbound refs are deferred.
		val boundRecv = if (dispatchIdx >= 0 && !hasExt) node.arguments.getOrNull(dispatchIdx) else null
		val ownerClass = fn.parent as? IrClass
		if (boundRecv != null && ownerClass != null && clrName(ownerClass) == null) {
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			return """{"k":"newBoundDelegate","ownerType":${fqnJson(typeName(ownerClass))},"method":${str(fn.name.asString())},"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${funcTypeOf(fn).toJson()}}"""
		}
		// `Class::method` (UNbound) -> a lifted static `__mref(self, args) = self.method(args)`; the receiver
		// becomes the delegate's first parameter. User classes only (`Func<UserType,…>` resolves via DelegateCtor).
		if (dispatchIdx >= 0 && boundRecv == null && !hasExt && ownerClass != null && clrName(ownerClass) == null) {
			val selfT = birType(fn.parameters[dispatchIdx].type)
			val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }
			val lname = "__mref${lambdaCounter++}"
			val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
				ps.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
			val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			val callE = """{"k":"callInstance","ownerType":${fqnJson(typeName(ownerClass))},"virtual":$virtual,"recv":{"k":"local","name":"__self"},"method":${str(fn.name.asString())},"args":[$argsJson]}"""
			val retVoid = fn.returnType.isUnit()
			val retT = birType(fn.returnType)
			val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
			val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + ps.map { it.type } + listOf(fn.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + ps.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
		}
		// A .NET method reference. Bound `obj::m` -> a delegate over the .NET instance method (ldftn). Unbound
		// `NetType::m` -> a lifted static `__mref(self, args) = self.m(args)` via clrInstance.
		val clrOwner = ownerClass?.let { clrName(it) }
		if (clrOwner != null && !hasExt) {
			val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }
			val argTypes = regs.joinToString(",") { birType(it.type).toJson() }
			val member = objectMethodName(fn) ?: fn.name.asString()
			val virtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT
			if (boundRecv != null)
				return """{"k":"newBoundClrDelegate","clrType":${fqnJson(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${funcTypeOf(fn).toJson()}}"""
			if (dispatchIdx >= 0) {
				val selfT = birType(fn.parameters[dispatchIdx].type)
				val lname = "__mref${lambdaCounter++}"
				val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
					regs.map { """{"name":${str(it.name.asString())},"type":${birType(it.type).toJson()}}""" }).joinToString(",")
				val argsJson = regs.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retVoid = fn.returnType.isUnit()
				val retT = birType(fn.returnType)
				// A genuine `NetType::m` method reference -> a lifted static forwarding to the .NET instance method.
				// (A kotlin.collections `Iterable::iterator` never reaches here: clrOwner is null for a jar-sourced stdlib
				// collection interface, so the enumerator-bridge routing lives in bir2cir Rule 5, not this clrOwner!=null path.)
				val callE = """{"k":"callInstance","ownerType":${str(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"ret":${birType(fn.returnType).toJson()},"recv":{"k":"local","name":"__self"},"args":[$argsJson]}"""
				val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
				val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + regs.map { it.type } + listOf(fn.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { tvOf(it).toJson() }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
				return """{"k":"newDelegate","method":${str(lname)},"funcType":${TypeNode.Fn(false, retT, listOf(selfT) + regs.map { birTypeDeleg(it.type) }).toJson()}$typeArgs}"""
			}
		}
		return unsupported(node, "a method reference to a .NET method (`::${fn.name}`)",
			"wrap the call in a lambda instead, e.g. `{ a -> x.${fn.name}(a) }`")
	}

	/**
	 * The materialization of a delegate accessor's compiler-synthesized `KProperty` argument (a `getValue`/
	 * `setValue`/`provideDelegate` 2nd arg, IR origin `PROPERTY_REFERENCE_FOR_DELEGATE`) -> `new
	 * kotlin.reflect.ClrPropertyStub(name)`, the REAL emitted stdlib name-only impl of `KProperty<Any?>`
	 * (KCallable.name + KAnnotatedElement.annotations). Kotlin's own delegate convention only ever reads `.name`
	 * off this argument in ordinary bodies — never get()/set()/invoke() — so this cheap stub (not the full
	 * `propertyRef` lift below) is always correct for it. Replaces the retired `dotkt$KPropertyImpl` synthetic.
	 */
	internal fun kPropertyStub(name: String): String =
		"""{"k":"new","type":${TypeNode.Fqn("kotlin.reflect.ClrPropertyStub", listOf(OBJ)).toJson()},"args":[{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(name)}}]}"""

	/**
	 * A genuine callable reference to a property (`::x`, `obj::p`, `Type::p`) -> a lifted class implementing the
	 * REAL emitted stdlib `kotlin.reflect.KProperty0`/`KMutableProperty0`/`KProperty1`/`KMutableProperty1<…>`
	 * interface (mirrors `samConversion`: a KProperty interface has no faithful .NET delegate representation, so
	 * this is a lifted CLASS via `new`, not a `newDelegate`). `node.type` already carries FIR's resolved interface
	 * identity + its (possibly generic) V/T arguments — reused verbatim as both the `interfaces` entry and the
	 * get/set param types, so a captured enclosing generic (`Box<T>::value`) resolves through the SAME `birType`/
	 * `typeArgSubst` machinery any other reference does (no separate remap here).
	 *
	 * v1 scope: a TOP-LEVEL property (`::x`), or a MEMBER property either BOUND (`obj::p`, receiver captured in a
	 * field) or UNBOUND (`Type::p`, receiver becomes the `get`/`set`'s own leading param) — mirrors `functionRef`'s
	 * ctor-ref/bound/unbound split. An EXTENSION-receiver property reference (`KProperty2`), a `lateinit var`, and
	 * a `@ClrField`/.NET-interface-overriding property are clean deferrals (their access shape differs from the
	 * plain get_/set_ accessor convention used below). The compiler-synthesized KProperty argument of a delegate's
	 * getValue/setValue/provideDelegate is NOT this path — those call sites materialize `kPropertyStub` directly
	 * without going through `expr()`/this dispatch; the origin check below is a defensive fallback only.
	 */
	internal fun propertyRef(node: IrPropertyReference): String {
		if (node.origin == IrStatementOrigin.PROPERTY_REFERENCE_FOR_DELEGATE)
			return kPropertyStub(node.symbol.owner.name.asString())
		if (propRefExtensionReceiver(node) != null)
			return unsupported(node, "this property reference",
				"an extension-receiver property reference (KProperty2) has no supported lowering yet")
		val prop = node.symbol.owner
		if (prop.isLateinit || isClrField(prop))
			return unsupported(node, "this property reference",
				"a lateinit/@ClrField property reference has no supported lowering yet")
		val getterFn = node.getter?.owner ?: prop.getter
			?: return unsupported(node, "this property reference", "the referenced property has no getter")
		if (clrIfaceMemberName(getterFn) != null)
			return unsupported(node, "this property reference",
				"a property overriding a .NET-mapped interface member has no supported lowering yet")
		val setterFn = if (prop.isVar) (node.setter?.owner ?: prop.setter) else null
		val declClass = getterFn.parent as? IrClass
		val name = prop.name.asString()
		val boundRecv = propRefDispatchReceiver(node)

		val ifaceSpec = birType(node.type) as? TypeNode.Fqn
			?: return unsupported(node, "this property reference",
				"its inferred type was not a KProperty/KMutableProperty interface")
		val ifaceArgs = ifaceSpec.args.orEmpty()
		val arity0 = ifaceSpec.name == "kotlin.reflect.KProperty0" || ifaceSpec.name == "kotlin.reflect.KMutableProperty0"
		val vType = ifaceArgs.lastOrNull() ?: OBJ
		val recvTypeNode = ifaceArgs.getOrNull(0).takeIf { !arity0 }   // KProperty1/KMutableProperty1's T (unbound only)

		val bound = declClass != null && arity0 && boundRecv != null
		val unbound = declClass != null && !arity0
		val cname = "dotkt\$${synthScope}\$PropRef${closureCounter++}"

		fun recvExprIn(): String = when {
			bound -> """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv"}"""
			unbound -> """{"k":"local","name":"receiver"}"""
			else -> """{"k":"this"}"""
		}
		val memberOwner: TypeNode = when {
			bound -> ownerSpec(declClass, boundRecv!!.type)
			unbound -> recvTypeNode ?: OBJ
			else -> OBJ
		}
		fun accessorCall(isSetter: Boolean, extraArg: String?): String {
			val fn = if (isSetter) setterFn!! else getterFn
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			val args = listOfNotNull(extraArg).joinToString(",")
			val method = str((if (isSetter) "set_" else "get_") + name)
			return """{"k":"callInstance","ownerType":${memberOwner.toJson()},"virtual":$virtual,"recv":${recvExprIn()},"method":$method,"args":[$args]}"""
		}

		val readBody: String = if (declClass == null) {
			// Top-level property: mirrors the ordinary top-level property-read path (a plain val/var is a static
			// field; a computed one — no backing field — is a get_<name>() static).
			val owner = fileClassOf(prop)
			if (prop.backingField == null)
				"""{"k":"return","value":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("get_$name")},"args":[]}}"""
			else """{"k":"return","value":{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(name)}}}"""
		} else """{"k":"return","value":${accessorCall(false, null)}}"""
		val readParams = if (unbound) """{"name":"receiver","type":${str(recvTypeNode ?: OBJ)}}""" else ""
		val getMethod = """{"name":"get","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""
		// KProperty0/KProperty1's declared supertype `() -> V`/`(T) -> V` gives them a REAL fake-overridden `invoke`
		// abstract member (confirmed in the compiled BIR: `interfaces` drops the FunctionN supertype — a Kotlin
		// function type has no faithful CLR interface base — but the interface's OWN `methods` still carries the
		// fake override AS ITS OWN abstract slot). So the lifted class must implement it too, same body as `get`
		// (mirrors JVM's `PropertyReferenceImpl.invoke() = get()`).
		val invokeMethod = """{"name":"invoke","static":false,"override":true,"virtual":true,"params":[$readParams],"ret":${str(vType)},"body":[$readBody]}"""

		val setMethod: String? = setterFn?.let {
			val setBody = if (declClass == null) {
				val owner = fileClassOf(prop)
				if (prop.backingField == null)
					"""{"k":"exprStmt","expr":{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("set_$name")},"args":[{"k":"local","name":"value"}]}}"""
				else """{"k":"exprStmt","expr":{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(name)},"value":{"k":"local","name":"value"}}}"""
			} else """{"k":"exprStmt","expr":${accessorCall(true, """{"k":"local","name":"value"}""")}}"""
			val setParams = (if (unbound) """{"name":"receiver","type":${str(recvTypeNode ?: OBJ)}},""" else "") +
				"""{"name":"value","type":${str(vType)}}"""
			"""{"name":"set","static":false,"override":true,"virtual":true,"params":[$setParams],"ret":${str(TypeNode.Fqn("kotlin.Unit"))},"body":[$setBody]}"""
		}

		val nameMethod = """{"name":"get_name","static":false,"override":true,"virtual":true,"params":[],"ret":${str(TypeNode.Fqn("kotlin.String"))},"body":[{"k":"return","value":{"k":"const","type":${fqnJson("kotlin.String")},"value":${str(name)}}}]}"""
		val annotationsRet = TypeNode.Fqn("kotlin.collections.List", listOf(TypeNode.Fqn("kotlin.Annotation")))
		val annotationsMethod = """{"name":"get_annotations","static":false,"override":true,"virtual":true,"params":[],"ret":${str(annotationsRet)},"body":[{"k":"return","value":{"k":"callStatic","owner":null,"method":"emptyList","typeArgs":[${str(TypeNode.Fqn("kotlin.Annotation"))}],"args":[]}}]}"""

		val recvFieldType = if (bound) birType(boundRecv!!.type) else null
		val fields = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
		val ctorParams = if (bound) """{"name":"__recv","type":${str(recvFieldType!!)}}""" else ""
		val ctorBody = if (bound) """{"k":"setField","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":"__recv","value":{"k":"local","name":"__recv"}}""" else ""

		val freeTps = freeTypeParams(listOf(node.type) + listOfNotNull(boundRecv?.type))
		val methods = listOfNotNull(getMethod, invokeMethod, setMethod, nameMethod, annotationsMethod).joinToString(",")
		liftedTypes.add("""{"name":${str(cname)},"kind":"class","generated":true${typeParamsJson(freeTps)},"base":null,"interfaces":[${ifaceSpec.toJson()}],"fields":[$fields],"ctors":[{"params":[$ctorParams],"baseArgs":null,"body":[$ctorBody]}],"methods":[$methods]}""")

		val classType = if (freeTps.isEmpty()) TypeNode.Fqn(cname) else TypeNode.Fqn(cname, freeTps.map { tvOf(it) })
		val ctorArgs = if (bound) expr(boundRecv!!) else ""
		return """{"k":"new","type":${classType.toJson()},"args":[$ctorArgs]}"""
	}

	/**
	 * Inline a scope function `recv.let/run/with/apply/also { ... }` to a value-block: bind the receiver to
	 * a unique local, rewrite `it`/`this` to it, then yield the lambda's last expression (let/run/with) or
	 * the receiver (apply/also). No delegate — the lambda body is spliced in directly.
	 */
	internal fun inlineScope(fq: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
		val fn = lambda.function
		// A suspending call inside an INLINE scope-function lambda used as a sub-expression (e.g. an expression body
		// `= with(lib){ b.fetch() }`, or `c.apply{ s() }.x`) inlines to a value-block whose stmts/result span a
		// suspension. kotc emits that value-block VERBATIM (the suspend call keeps its `"suspendCall"` tag); the
		// downstream coroutine lowering (bir2cir SuspendColdLowering) flattens the value-block and segments the
		// suspension as an ordinary suspension point. kotc holds NO coroutine knowledge here (#11).
		val vname = "__scope${scopeCounter++}"
		val recvInit = expr(recvExpr)   // emit the receiver expression before binding `it`/`this`
		val recvParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		recvParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val returnsRecv = fq == "kotlin.apply" || fq == "kotlin.also"
		val init = ArrayList<String>()
		init.add("""{"k":"var","name":${str(vname)},"type":${birType(recvExpr.type).toJson()},"init":$recvInit}""")
		val result: String
		if (returnsRecv) {
			stmts.forEach { if (it !is IrReturn) init.add(stmt(it)) }   // body is side-effects; Unit returns dropped
			result = """{"k":"local","name":${str(vname)}}"""
		} else {
			stmts.dropLast(1).forEach { init.add(stmt(it)) }
			result = when (val last = stmts.lastOrNull()) {
				is IrReturn -> expr(last.value)
				is IrExpression -> expr(last)
				else -> { last?.let { init.add(stmt(it)) }; """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
			}
		}
		recvParam?.let { valSubst.remove(it.name.asString()) }
		itParam?.let { valSubst.remove(it.name.asString()) }
		return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
	}

	/** `r.use { block }` -> a value-block: `var r; var res; try { res = block(r) } finally { r.Dispose() }; res`. */
	internal fun inlineUse(recvExpr: IrExpression, lambda: IrFunctionExpression, retType: TypeNode): String {
		val fn = lambda.function
		val uname = "__use${scopeCounter++}"; val rname = "__useRes${scopeCounter++}"
		val recvInit = expr(recvExpr)
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(uname)}}""" }
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		// kotc now emits the Kotlin FQN for source types, so a Unit-returning block's type is "kotlin.Unit"
		// (bir2cir lowers it to void). Accept the residual "void" shorthand too (synthetic/already-lowered rets).
		val unit = retType == TypeNode.Fqn("kotlin.Unit")
		val tryBody = ArrayList<String>()
		stmts.dropLast(1).forEach { tryBody.add(stmt(it)) }
		when (val last = stmts.lastOrNull()) {
			is IrReturn -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last.value)}}""") else last.value.takeIf { !it.type.isUnit() }?.let { tryBody.add("""{"k":"exprStmt","expr":${expr(it)}}""") }
			is IrExpression -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last)}}""") else tryBody.add("""{"k":"exprStmt","expr":${expr(last)}}""")
			else -> last?.let { tryBody.add(stmt(it)) }
		}
		itParam?.let { valSubst.remove(it.name.asString()) }
		// The `use{}` try/finally structure is a language lowering that stays in kotc, but the `close()` call in the finally
		// is a PLAIN Kotlin member call on the kotlin.AutoCloseable receiver — bir2cir substitutes it to
		// System.IDisposable.Dispose() off the @ClrTypeAlias/@ClrIntrinsic("Dispose") binding (layer purity — no BCL name
		// in kotc). `use`'s signature (`T : AutoCloseable?`) guarantees the owner is kotlin.AutoCloseable.
		val dispose = """{"k":"exprStmt","expr":{"k":"callInstance","ownerType":${fqnJson("kotlin.AutoCloseable")},"method":"close","virtual":true,"recv":{"k":"local","name":${str(uname)}},"args":[]}}"""
		val tryNode = """{"k":"try","type":${fqnJson("kotlin.Unit")},"body":[${tryBody.joinToString(",")}],"catches":[],"finally":[$dispose]}"""
		val init = ArrayList<String>()
		init.add("""{"k":"var","name":${str(uname)},"type":${birType(recvExpr.type).toJson()},"init":$recvInit}""")
		if (!unit) init.add("""{"k":"var","name":${str(rname)},"type":${retType.toJson()}}""")
		init.add(tryNode)
		val result = if (unit) """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" else """{"k":"local","name":${str(rname)}}"""
		return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
	}

	internal fun hasLambdaArg(call: IrCall): Boolean = regularArgs(call).any {
		it is IrFunctionExpression || ((it as? IrGetValue)?.symbol?.owner?.let { owner -> inlineLambdas.containsKey(owner) } == true)
	}

	internal fun nestedCapturesValue(node: IrElement?, decl: IrValueDeclaration): Boolean {
		var found = false
		node?.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				if (found) return
				when (element) {
					is IrFunctionExpression -> {
						if (decl in capturedVars(element.function, includeThis = true)) {
							found = true
							return
						}
					}
					is IrClass -> {
						if (decl in capturedVarsForObject(element)) {
							found = true
							return
						}
					}
				}
				element.acceptChildrenVoid(this)
			}
		})
		return found
	}


	/** Statements of a function/lambda body (block body, or a single-expression `= expr` body). */
	internal fun bodyStatements(body: org.jetbrains.kotlin.ir.IrElement?): List<org.jetbrains.kotlin.ir.IrStatement> = when (body) {
		is IrBlockBody -> body.statements
		is IrExpressionBody -> listOf(body.expression)
		else -> emptyList()
	}

	/**
	 * Real inlining of a USER `inline fun` that takes a lambda arg ([[function-inlining-spike]]): bind non-lambda
	 * params to temps and lambda params to the passed lambdas (in `inlineLambdas`), then splice the callee body as a
	 * value-block. Invocations of a lambda param inside the body splice that lambda (see spliceLambdaCall); a
	 * non-local `return` (already targeting the enclosing fun in the IR) returns from the caller since valueBlock is
	 * inline. This also fixes mutable capture (the captured `var` is the caller's own local). lambda-less inline funs
	 * never reach here — they emit as ordinary delegate-taking calls (the JIT inlines them).
	 */
	internal fun inlineCall(call: IrCall): String {
		val callee = call.symbol.owner
		val extParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val params = regularParams(callee)
		val extArg = extensionReceiver(call)
		val args = regularArgs(call)
		val pre = ArrayList<String>()
		val boundVals = ArrayList<String>(); val boundLams = ArrayList<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration>()
		val oldVals = HashMap<String, String>()
		val hadOldVals = HashSet<String>()
		var boundExt = false
		fun bindVal(name: String, ref: String) {
			if (!boundVals.contains(name)) {
				if (valSubst.containsKey(name)) {
					hadOldVals.add(name)
					oldVals[name] = valSubst[name]!!
				}
				boundVals.add(name)
			}
			valSubst[name] = ref
		}
		// Substitute the callee's type params with the call's type args FIRST — before binding params — so both a bound
		// param's temp type (`birType(p.type)`) AND the spliced body resolve `gp:T` to the inferred type (a `*` star
		// projection with no concrete arg -> `object`/Any?). E.g. `with(e){…}`'s receiver temp gets `@Entry`, not `gp:T`.
		val tps = callee.typeParameters
		val subKeys = ArrayList<IrTypeParameter>()
		val calleeTypeArgs = HashMap<IrTypeParameter, TypeNode>()
		val oldTypeArgs = HashMap<IrTypeParameter, TypeNode?>()
		val hadOldTypeArg = HashSet<IrTypeParameter>()
		for (i in tps.indices) {
			val tp = tps[i]
			if (typeArgSubst.containsKey(tp)) {
				hadOldTypeArg.add(tp)
				oldTypeArgs[tp] = typeArgSubst[tp]
			}
			val ta = call.typeArguments.getOrNull(i)
			val bt = ta?.let { birType(it) }
			// "Self star" = the arg IS the callee's OWN type param (unresolved) -> object. Discriminate by SYMBOL
			// identity, not the token string: the CALLER's param with the SAME NAME also prints `gp:T`
			// (mapNotNullTo<T,..> body calling forEach<T> with the outer T) and is perfectly resolved — erasing it
			// to object detached the splice from the enclosing generic (Iterable[object] temp, object element into
			// Func<!!T,..>.Invoke -> InvalidProgramException).
			val selfOwned = ((ta as? IrSimpleType)?.classifierOrNull as? IrTypeParameterSymbol)?.owner?.parent == callee
			val subst = if (bt == null || selfOwned) OBJ else bt
			calleeTypeArgs[tp] = subst
			typeArgSubst[tp] = subst
			subKeys.add(tp)
		}
		fun restoreCalleeTypeArgs() {
			for (tp in subKeys) typeArgSubst[tp] = calleeTypeArgs[tp]!!
		}
		fun <T> withCallerTypeArgs(block: () -> T): T {
			for (tp in subKeys) {
				if (hadOldTypeArg.contains(tp)) typeArgSubst[tp] = oldTypeArgs[tp]!!
				else typeArgSubst.remove(tp)
			}
			return try { block() } finally { restoreCalleeTypeArgs() }
		}
		val callerTypeScope = TypeArgScope(subKeys.toList(), HashMap(oldTypeArgs), HashSet(hadOldTypeArg))
		// A MEMBER inline fun's DISPATCH receiver must be bound like the extension receiver: the spliced body's
		// `this` (IrGetValue of the callee's dispatch param) otherwise falls through to the CALLER's `{"k":"this"}` —
		// `absoluteValue.toComponents { … }` inside Duration.toString read the NEGATIVE outer duration instead of
		// absoluteValue (printed "--1s"), and in a static caller a bare `this` is not even valid.
		val dispatchParam = callee.parameters.firstOrNull { it.kind == IrParameterKind.DispatchReceiver }
		val dispatchArg = dispatchReceiver(call)
		var boundDispatch = false
		if (dispatchParam != null && dispatchArg != null) {
			val tmp = "__inl${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(dispatchParam.type).toJson()},"init":${withCallerTypeArgs { expr(dispatchArg) }}}""")
			selfSubst[dispatchParam] = """{"k":"local","name":${str(tmp)}}"""
			boundDispatch = true
		}
		if (extParam != null && extArg != null) {
			val tmp = "__inl${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(extParam.type).toJson()},"init":${withCallerTypeArgs { expr(extArg) }}}""")
			val ref = """{"k":"local","name":${str(tmp)}}"""
			selfSubst[extParam] = ref
			if (extParam.name.asString() != "<this>") {
				bindVal(extParam.name.asString(), ref)
			}
			boundExt = true
		}
		for ((p, arg) in params.zip(args)) {
			// A `crossinline`/`noinline` lambda is NOT spliced: crossinline guarantees no non-local return (the only
			// reason to splice — see [[clr-not-jvm-discard-jvmisms]]) and noinline forbids inlining outright, and both
			// may be invoked from a nested lambda/object. Bind them to a real delegate local (the `else` path): the
			// arg emits as a closure, `block()` falls through to the delegate-invoke path, and a nested lambda/object
			// captures the local via the normal closure machinery.
			val inlineLambdaArg = when (arg) {
				is IrFunctionExpression -> arg
				is IrGetValue -> inlineLambdas[arg.symbol.owner]
				else -> null
			}
			if (inlineLambdaArg != null && !p.isCrossinline && !p.isNoinline && !nestedCapturesValue(callee.body, p)) {
				inlineLambdas[p] = inlineLambdaArg
				inlineLambdaTypeScopes[inlineLambdaArg] = callerTypeScope
				boundLams.add(p)
			}
			else {
				val tmp = "__inl${inlCounter++}"
				pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(p.type).toJson()},"init":${withCallerTypeArgs { expr(inlineLambdaArg ?: arg) }}}""")
				bindVal(p.name.asString(), """{"k":"local","name":${str(tmp)}}""")
			}
		}
		val result = spliceBodyWithReturns(callee, callee.returnType.isUnit(), pre)
		boundVals.forEach { name -> if (hadOldVals.contains(name)) valSubst[name] = oldVals[name]!! else valSubst.remove(name) }
		boundLams.forEach { inlineLambdas.remove(it)?.let { lam -> inlineLambdaTypeScopes.remove(lam) } }
		subKeys.forEach { tp -> if (hadOldTypeArg.contains(tp)) typeArgSubst[tp] = oldTypeArgs[tp]!! else typeArgSubst.remove(tp) }
		if (boundExt) selfSubst.remove(extParam)
		if (boundDispatch) selfSubst.remove(dispatchParam)
		// The `suspendCoroutineUninterceptedOrReturn { c -> … }` intrinsic: after inlining, its fake body
		// (`throw NotImplementedError("… is intrinsic")`) survives as this valueBlock's result, and the crossinline
		// `block` is materialized as a closure captured into a dead __inlN. bir2cir recognizes such a block as a cold
		// suspension point. Stamp a STABLE `suspendIntrinsic:true` marker so bir2cir need not sniff the fake body's
		// thrown-message string (SuspendColdLowering.IsSuspendIntrinsicBlock prefers this flag; the string path is
		// legacy fallback). kotc emits the flag, NOT any CLR knowledge — it's a Kotlin-language intrinsic identity.
		val suspendIntrinsic = if (callee.fqNameWhenAvailable?.asString() ==
			"kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn") ""","suspendIntrinsic":true""" else ""
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result$suspendIntrinsic}"""
	}

	internal fun <T> withTypeArgScope(scope: TypeArgScope?, block: () -> T): T {
		if (scope == null) return block()
		val saved = HashMap<IrTypeParameter, TypeNode?>()
		val hadSaved = HashSet<IrTypeParameter>()
		for (nm in scope.keys) {
			if (typeArgSubst.containsKey(nm)) {
				hadSaved.add(nm)
				saved[nm] = typeArgSubst[nm]
			}
			if (scope.had.contains(nm)) typeArgSubst[nm] = scope.old[nm]!!
			else typeArgSubst.remove(nm)
		}
		return try { block() } finally {
			for (nm in scope.keys) {
				if (hadSaved.contains(nm)) typeArgSubst[nm] = saved[nm]!!
				else typeArgSubst.remove(nm)
			}
		}
	}

	/** CROSS-MODULE inline splice: a call to an injected `inline fun` (its body lives in [KotlinInline] on the
	 *  referenced assembly, read by ilemit at splice time). We carry the call's bindings — each regular param's arg
	 *  value, or for a lambda param the lambda's param name + body (emitted in the CALLER's scope, so a non-local
	 *  `return` in it becomes the caller's return). ilemit substitutes these into the carried body. */
	internal fun inlineSpliceCall(call: IrCall, fileClass: String): String {
		val callee = call.symbol.owner
		val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
		val args = regularArgs(call)
		val bindings = params.mapIndexed { i, p ->
			val arg = args.getOrNull(i)
			if (arg is IrFunctionExpression) {
				val lamParam = arg.function.parameters.firstOrNull { it.kind == IrParameterKind.Regular }?.name?.asString() ?: "it"
				val body = bodyStatements(arg.function.body).joinToString(",") { stmt(it) }
				"""{"name":${str(p.name.asString())},"lambdaParam":${str(lamParam)},"lambdaBody":[$body]}"""
			} else """{"name":${str(p.name.asString())},"value":${arg?.let { expr(it) } ?: "null"}}"""
		}.joinToString(",")
		// An EXTENSION inline fun's body references the receiver via `this`; carry it so EmitInlineSplice can substitute it
		// (the body's `this` -> this bound value). Non-extension splices omit it (unchanged).
		val thisJson = extensionReceiver(call)?.let { ""","thisValue":${expr(it)}""" } ?: ""
		// Disambiguate the file-facade overload (forEach/count/... exist for Iterable/Array/CharSequence): the .NET method's
		// param count = regular params + the receiver-as-__self, and its generic arity = the fn's type params.
		val pc = params.size + (if (extensionReceiver(call) != null) 1 else 0)
		val ga = callee.typeParameters.size
		return """{"k":"inlineSplice","type":${str(fileClass)},"method":${str(callee.name.asString())},"pc":$pc,"ga":$ga,"bindings":[$bindings]$thisJson}"""
	}

	/** Splice an invoked inlined lambda `f(args)`: bind its params to the invoke args, then splice its body. */
	internal fun spliceLambdaCall(lambda: IrFunctionExpression, call: IrCall): String {
		val fn = lambda.function
		val extParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val args = regularArgs(call)
		val extArg = if (extParam != null) extensionReceiver(call) ?: args.firstOrNull() else null
		val regArgs = if (extParam != null && extArg != null && extensionReceiver(call) == null && args.firstOrNull() === extArg) args.drop(1) else args
		val pre = ArrayList<String>(); val bound = ArrayList<String>(); var boundExt = false
		if (extParam != null && extArg != null) {
			val tmp = "__lam${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(extParam.type).toJson()},"init":${expr(extArg)}}""")
			val ref = """{"k":"local","name":${str(tmp)}}"""
			selfSubst[extParam] = ref
			valSubst[extParam.name.asString()] = ref
			bound.add(extParam.name.asString())
			boundExt = true
		}
		for ((p, arg) in params.zip(regArgs)) {
			val tmp = "__lam${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${birType(p.type).toJson()},"init":${expr(arg)}}""")
			valSubst[p.name.asString()] = """{"k":"local","name":${str(tmp)}}"""; bound.add(p.name.asString())
		}
		val result = withTypeArgScope(inlineLambdaTypeScopes[lambda]) {
			spliceBodyWithReturns(fn, fn.returnType.isUnit() || call.type.isUnit(), pre)
		}
		bound.forEach { valSubst.remove(it) }
		if (boundExt) selfSubst.remove(extParam)
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	/** True iff [body] contains an IrReturn TARGETING [target] anywhere other than as the body's LAST top-level
	 *  statement (spliceBody already folds a tail return into the value expression). Nested lambdas are walked too:
	 *  a labeled return inside one can target the enclosing spliced fn. */
	internal fun hasEarlyReturn(body: org.jetbrains.kotlin.ir.IrElement?, target: org.jetbrains.kotlin.ir.symbols.IrReturnTargetSymbol): Boolean {
		val stmts = bodyStatements(body)
		val tail = stmts.lastOrNull()
		var found = false
		val walker = object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				if (found) return
				if (element is IrReturn && element.returnTargetSymbol == target) { found = true; return }
				element.acceptChildrenVoid(this)
			}
		}
		for (s in stmts) {
			// The tail return itself is fine (spliceBody folds it) — but its VALUE could still nest one.
			if (s === tail && s is IrReturn && s.returnTargetSymbol == target) s.value.acceptVoid(walker)
			else s.acceptVoid(walker)
			if (found) return true
		}
		return false
	}

	/**
	 * spliceBody + EARLY-return support. A `return v` in the middle of a spliced inline body (indexOfLast's
	 * `return index` inside its loop) must not emit a raw method return — the splice is a valueBlock INSIDE the
	 * caller, so the raw return used the CALLER's frame (a void caller got an Int32 on the stack at ret:
	 * kotlin.time.Duration.appendFractional, ilverify ReturnVoid + InvalidProgramException at run). Route every
	 * return targeting the spliced fn through a RESULT LOCAL + an END LABEL (`res = v; goto end`; the natural tail
	 * value assigns res too; the valueBlock result reads res after the label). Early-return-free bodies (the
	 * overwhelmingly common case) keep the plain spliceBody shape — zero BIR churn.
	 */
	internal fun spliceBodyWithReturns(target: IrSimpleFunction, unit: Boolean, pre: MutableList<String>): String {
		val stmts = bodyStatements(target.body)
		if (!hasEarlyReturn(target.body, target.symbol)) return spliceBody(stmts, unit, pre)
		val res = if (unit) null else "__inlRet${inlCounter++}"
		val end = cfgFresh()
		res?.let {
			val rt = birType(target.returnType)
			pre.add("""{"k":"var","name":${str(it)},"type":${str(rt)},"init":{"k":"default","type":${str(rt)}}}""")
		}
		val saved = inlineReturnSubst[target.symbol]
		inlineReturnSubst[target.symbol] = res to end
		// A NOTHING-typed tail expression (a when whose branches all return/throw) is a STATEMENT, not the splice
		// value — its returns route through the subst; reading it as the value would render IrReturn as an expr.
		val last = stmts.lastOrNull()
		val tail = if (!unit && last is IrExpression && last !is IrReturn && last.type.classFqName?.asString() == "kotlin.Nothing") {
			stmts.forEach { pre.add(stmt(it)) }
			null
		} else spliceBody(stmts, unit, pre)
		if (saved != null) inlineReturnSubst[target.symbol] = saved else inlineReturnSubst.remove(target.symbol)
		return if (res != null) {
			tail?.let { pre.add("""{"k":"setLocal","name":${str(res)},"value":$it}""") }
			pre.add("""{"k":"label","id":$end}""")
			"""{"k":"local","name":${str(res)}}"""
		} else {
			pre.add("""{"k":"label","id":$end}""")
			"""{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
		}
	}

	/** Emit body statements into `pre`, returning the value expression (Unit -> void const; else the last expr). */
	internal fun spliceBody(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, unit: Boolean, pre: MutableList<String>): String {
		if (unit) { stmts.forEach { pre.add(stmt(it)) }; return """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
		stmts.dropLast(1).forEach { pre.add(stmt(it)) }
		return when (val last = stmts.lastOrNull()) {
			is IrReturn -> expr(last.value)
			is IrExpression -> expr(last)
			else -> { last?.let { pre.add(stmt(it)) }; """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}""" }
		}
	}

	/** Lift a local function to a file-class static method; captured vars become leading params (by their own names). */
	internal fun liftLocalFn(fn: IrSimpleFunction) {
		// Captured vars (incl. the enclosing `this`) become leading params; the call site prepends their values.
		val captures = capturedVars(fn, includeThis = true)
		val lname = "__local${scopeCounter++}_${fn.name.asString()}"
		// A local fn referencing an enclosing type parameter (in a capture, its own params, or its return) becomes a
		// GENERIC static method — reified CLR generics, same as a capturing closure class. The call site (callStatic)
		// passes the enclosing type params as type arguments.
		val ownRegParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val freeTps = freeTypeParams(captures.map { it.type } + ownRegParams.map { it.type } + listOf(fn.returnType))
		localFns[fn] = Triple(lname, captures, freeTps)
		fun pj(name: String, t: IrType) = """{"name":${str(name)},"type":${birType(t).toJson()}}"""
		val capPairs = captures.map { it to captureFieldName(it) }
		// Captures arrive as leading params; rewrite body refs to those params. This must cover not only `<this>` but
		// also receiver-like captured params such as `$this$buildString`, otherwise an active inline substitution can
		// leak a caller-local (`__lam<N>`) into the lifted method body.
		capPairs.forEach { (decl, fname) -> captureSubst[decl] = """{"k":"local","name":${str(fname)}}""" }
		val capParams = capPairs.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		val ownParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { pj(it.name.asString(), it.type) }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
		val ret = birType(fn.returnType)
		liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[${(capParams + ownParams).joinToString(",")}],"ret":${str(ret)},"body":[$body]}""")
	}

	/** `stackBuffer(n) { buf -> body }` -> a scoped CLR stack allocation: declare a length + a localloc'd pointer,
	 *  splice the (inline) block with `buf` bound to that allocation, return the block's result R. */
	internal fun emitStackBuffer(call: IrCall): String {
		val args = regularArgs(call)
		val lambda = args.getOrNull(1) as? IrFunctionExpression
			?: return unsupported(call, "stackBuffer", "its block must be a literal lambda (so it can be inlined into the caller's frame)")
		val fn = lambda.function
		val bufParam = fn.parameters.first { it.kind == IrParameterKind.Regular }
		val elemT = call.typeArguments.getOrNull(0)?.let { birType(it) } ?: OBJ
		val c = scopeCounter++
		val ptrName = "__sbp$c"; val lenName = "__sbl$c"
		val pre = arrayListOf(
			"""{"k":"var","name":${str(lenName)},"type":${fqnJson("kotlin.Int")},"init":${expr(args[0])}}""",
			"""{"k":"var","name":${str(ptrName)},"type":${fqnJson("stackptr")},"init":{"k":"stackAlloc","count":{"k":"local","name":${str(lenName)}},"elem":${str(elemT)}}}""")
		stackBufSubst[bufParam] = StackBufInfo(ptrName, lenName, elemT)
		val result = spliceBody(bodyStatements(fn.body), fn.returnType.isUnit() || call.type.isUnit(), pre)
		stackBufSubst.remove(bufParam)
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	/** A `StackBuffer<T>` member access (`buf[i]` / `buf[i]=v` / `buf.size`) inside the spliced block -> a stack op. */
	internal fun emitStackBufferOp(call: IrCall, callee: IrSimpleFunction, info: StackBufInfo): String {
		val ptr = """{"k":"local","name":${str(info.ptrName)}}"""
		val len = """{"k":"local","name":${str(info.lenName)}}"""
		return when {
			callee.correspondingPropertySymbol?.owner?.name?.asString() == "size" -> len
			callee.name.asString() == "get" ->
				"""{"k":"stackGet","ptr":$ptr,"len":$len,"index":${expr(regularArgs(call)[0])},"elem":${str(info.elemT)}}"""
			callee.name.asString() == "set" ->
				"""{"k":"stackSet","ptr":$ptr,"len":$len,"index":${expr(regularArgs(call)[0])},"elem":${str(info.elemT)},"value":${expr(regularArgs(call)[1])}}"""
			// `buf.asSpan()` -> `new System.Span<T>(ptr, size)` over the stack memory (for .NET Span APIs).
			callee.name.asString() == "asSpan" -> """{"k":"stackAsSpan","ptr":$ptr,"len":$len,"elem":${str(info.elemT)}}"""
			else -> unsupported(call, "StackBuffer.${callee.name.asString()}", "only size / indexing / asSpan are supported")
		}
	}

	/** A `throw`-able exception construction node: a plain `new <KotlinExceptionFQN>(msg?)` on the PURE-KOTLIN
	 *  exception class (`kotlin.IllegalArgumentException` / `kotlin.IllegalStateException` / …). kotc names NO
	 *  `System.*` CLR exception type — it emits the Kotlin FQN identity exactly like a user `throw
	 *  IllegalArgumentException(msg)`, and bir2cir's MemberCallSubstitution.TransformNew resolves the @ClrTypeAlias
	 *  owner off the ref.dll to the BCL exception (`kotlin.IllegalArgumentException` -> `System.ArgumentException`).
	 *  This is the same code path a user throw already takes, so the emitted IL is identical.
	 *  (exception-map-to-clrtypealias, USER 2026-07-01.) `msgJson` is an already-quoted JSON string, or
	 *  null for the no-arg ctor. */
	internal fun newExc(type: String, msgJson: String?): String =
		if (msgJson != null) """{"k":"new","type":${fqnJson(type)},"argTypes":[${fqnJson("kotlin.String")}],"args":[{"k":"const","type":${fqnJson("kotlin.String")},"value":$msgJson}]}"""
		else """{"k":"new","type":${fqnJson(type)},"argTypes":[],"args":[]}"""

	internal fun throwExpr(exc: String): String = """{"k":"throwExpr","value":$exc}"""

	/** Free value references in a lambda body (referenced but not declared inside) = its captured vars. */
	internal fun capturedVars(fn: IrSimpleFunction, includeThis: Boolean = false): List<IrValueDeclaration> {
		val declared = HashSet<IrValueDeclaration>()
		fn.parameters.forEach { declared.add(it) }
		val referenced = LinkedHashSet<IrValueDeclaration>()
		fn.body?.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				when (element) {
					is IrVariable -> declared.add(element)
					// A nested lambda/local-fun's own parameters are declared there, not captured by `fn`.
					is IrValueParameter -> declared.add(element)
					is IrGetValue -> referenced.add(element.symbol.owner)
					is IrSetValue -> referenced.add(element.symbol.owner)
				}
				element.acceptChildrenVoid(this)
			}
		})
		return referenced.filter { it !in declared && (includeThis || it.name.asString() != "<this>") }
	}

	/**
	 * Free outer values captured by an object literal: any value referenced anywhere in the anon class
	 * (method bodies + property initializers) but declared OUTSIDE it. The anon's own receivers/params/locals
	 * are excluded by identity — crucially this keeps the captured enclosing `this` (same name "<this>" as
	 * the anon's own receiver, distinguished only by symbol identity).
	 */
	internal fun capturedVarsForObject(anon: IrClass): List<IrValueDeclaration> {
		val own = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
		val referenced = LinkedHashSet<IrValueDeclaration>()
		anon.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				when (element) {
					is IrValueParameter -> own.add(element)
					is IrVariable -> own.add(element)
					is IrGetValue -> referenced.add(element.symbol.owner)
					is IrSetValue -> referenced.add(element.symbol.owner)
				}
				element.acceptChildrenVoid(this)
			}
		})
		return referenced.filter { it !in own }
	}

	/** Value declarations assigned (IrSetValue) anywhere inside an object literal (for mutable-capture detection). */
	internal fun mutatedIn(node: IrElement): Set<IrValueDeclaration> {
		val out = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
		node.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				if (element is IrSetValue) out.add(element.symbol.owner)
				element.acceptChildrenVoid(this)
			}
		})
		return out
	}

	/** A capture's field name: the enclosing `this` -> `__outer`, an outer local/param -> its own name. */
	internal fun captureFieldName(d: IrValueDeclaration): String =
		if (d.name.asString() == "<this>") "__outer" else d.name.asString()

	/** A capture's value at the `new` site (in the enclosing context): the outer `this`, or an outer local. */
	internal fun capValueExpr(d: IrValueDeclaration): String =
		// Evaluate the capture VALUE in the enclosing context: honor an active substitution (e.g. an intrinsic
		// block's `c` bound to the coroutine's own continuation, or an outer capture field) before falling back.
		// `valSubst` is checked next so a captured inline parameter (a crossinline/noinline lambda bound to a
		// `__inl<N>` delegate local) is captured by its substituted local, mirroring IrGetValue's resolution order.
		captureSubst[d] ?: valSubst[d.name.asString()]
			?: if (d.name.asString() == "<this>") """{"k":"this"}""" else """{"k":"local","name":${str(d.name.asString())}}"""

	/**
	 * The lambda's value parameters in delegate order: the EXTENSION RECEIVER first (a receiver lambda
	 * `Scope.() -> Unit` is `Function1<Scope, Unit>`, so its receiver is the first delegate argument — and the body's
	 * implicit-receiver references resolve to it), then the regular params. Keeping this consistent with `birType`'s
	 * view of the function type (which derives args from the FunctionN type arguments, receiver included) is what
	 * makes `build { ... }` receiver-lambda DSLs work (feedback item 7).
	 */
	internal fun orderedLambdaParams(fn: IrSimpleFunction): List<IrValueParameter> =
		fn.parameters.filter { it.kind == IrParameterKind.ExtensionReceiver } +
			fn.parameters.filter { it.kind == IrParameterKind.Regular }

	/** The function type `fn` for a lambda's signature (extension receiver first). A `suspend` lambda sets
	 *  `fn.suspend=true` — same delegate shape carrying the suspend FACT for the newSuspendLambda SM builder.
	 *  bir2cir ERASES a suspend `fn` to `object` wherever it appears in a TYPE slot; only the `funcType` node
	 *  key itself keeps it. So this stays behavior-preserving. */
	internal fun funcTypeOf(fn: IrSimpleFunction): TypeNode.Fn {
		val ps = orderedLambdaParams(fn).map { birTypeDeleg(it.type) }
		return TypeNode.Fn(fn.isSuspend, funcRetTypeOf(fn.returnType), ps)
	}

	/**
	 * A function type's RETURN, preserving generic-parameter nullability: a `(T) -> R?` slot emits `nullable(tv)`
	 * (the Kotlin FACT that the func's return is nullable — otherwise LOST for an unconstrained generic). bir2cir
	 * CONSUMES the marker (a nullable-marked func return lowers to `object`, the erased CLR rep).
	 */
	internal fun funcRetTypeOf(t: IrType): TypeNode {
		if (t.isUnit()) return TypeNode.Fqn("kotlin.Unit")
		// birTypeDeleg already wraps a nullable core as `{t:nullable,of:...}` (uniform birType), incl. a nullable `tv`.
		return birTypeDeleg(t)
	}

	/**
	 * Like `birType`, for a delegate (Func/Action) signature slot. `KProperty*` is NO LONGER erased to Any here
	 * (#70): it is a REAL emitted stdlib interface now (KPropertyClr.kt), not a `dotkt$KProperty` synthetic
	 * TypeBuilder, so a `Delegates.observable`/`vetoable` callback's `(KProperty<*>, T, T) -> Unit` param carries
	 * its real generic identity like any other stdlib interface in a Func/Action slot.
	 */
	internal fun birTypeDeleg(t: IrType): TypeNode {
		// A Unit PARAM must be the real Unit VALUE identity, not `void` (a void param is invalid metadata); the RETURN
		// context special-cases Unit before calling this. The @/referenced-Unit decision is now bir2cir's.
		if (t.isUnit()) return TypeNode.Fqn("kotlin.Unit")
		return birType(t)
	}

	/** Lambda/closure method params with KProperty erased to Any (must agree with funcTypeOf for delegates):
	 *  extension receiver first (so a receiver lambda's `$this$build` is bound), then regular params. */
	internal fun lambdaParamsJson(params: List<IrValueParameter>): String =
		(params.filter { it.kind == IrParameterKind.ExtensionReceiver } + params.filter { it.kind == IrParameterKind.Regular })
			// A `Unit`-typed PARAMETER must be the real Unit VALUE identity, not `void` (invalid metadata).
			.joinToString(",") { p ->
				val ty = if (p.type.isUnit()) TypeNode.Fqn("kotlin.Unit") else birTypeDeleg(p.type)
				"""{"name":${str(p.name.asString())},"type":${ty.toJson()}}"""
			}

	/** The BIR placeholder for an OMITTED default argument this build cannot inline (a cross-module default whose VALUE
	 *  the frontend jar dropped → IrErrorExpression). Emitted POSITIONALLY so a later provided arg keeps its slot;
	 *  bir2cir's DefaultArgSplice replaces it (by array index) from the callee's ref.dll @KotlinDefault / [DefaultParameterValue]. */
	private val defaultArgPlaceholder = """{"k":"defaultArg"}"""
	private val defaultArgThisToken = """{"k":"this"}"""

	/** Regular args, POSITIONALLY complete, filling omitted default arguments (IL has no default-parameter mechanism).
	 *  Fill source by default KIND: a same-module CONSTANT/global default is inlined verbatim; a same-module default that
	 *  reads the callee's RECEIVER (`missingDelimiterValue = this`, a data-class `copy`'s `y = this.y`) is inlined with
	 *  `this` rewritten to THIS call's receiver (the JVM `$default` scope, done at the JSON level); a CROSS-MODULE default
	 *  (IrErrorExpression — the jar preserves no default VALUE) becomes a `defaultArg` placeholder that bir2cir fills from
	 *  the ref.dll. The placeholder is emitted whenever the callee carries @KotlinDefault OR a LATER arg is provided (a
	 *  "gap" — silently omitting it would shift the later arg into the wrong parameter slot: the joinToString/substringAfter
	 *  miscompile); a purely TRAILING cross-module omit on a metadata-representable callee is still dropped so ilemit's
	 *  [DefaultParameterValue] backfill fills it (unchanged). */
	internal fun filledArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> {
		val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
		val carries = (callee as? org.jetbrains.kotlin.ir.declarations.IrSimpleFunction)?.let { carriesKotlinDefault(it) } ?: false
		val receiverSyms = callee.parameters.filter {
			it.kind == IrParameterKind.DispatchReceiver || it.kind == IrParameterKind.ExtensionReceiver
		}.map { it.symbol }.toHashSet()
		val valueSyms = callee.parameters.filter { it.kind == IrParameterKind.Regular }.map { it.symbol }.toHashSet()
		// The call's receiver expression (for `this`-referencing same-module defaults): the extension receiver if any, else
		// the dispatch receiver (a data-class `copy` is a member, so its `this.y` default resolves to the dispatch receiver).
		// Emitted lazily and reused per omitted default — single-eval is best-effort (a trivial local/this receiver is safe
		// to duplicate; a side-effecting receiver read by several omitted defaults is a documented edge).
		val recvJson: String? by lazy { (extensionReceiver(call) ?: dispatchReceiver(call))?.let { expr(it) } }
		val regs = callee.parameters.mapIndexedNotNull { i, p -> if (p.kind == IrParameterKind.Regular) i to p else null }
		val provided = regs.map { (i, _) -> if (i < call.arguments.size) call.arguments[i] else null }
		val out = ArrayList<String>()
		// The filled JSON for each already-processed value parameter — the substitution source for a same-module default
		// that reads ANOTHER value parameter (`b: Int = a * 10`). A Kotlin default may reference only EARLIER params, so
		// every referenced param is already recorded here by the time its reader is processed.
		val filledByParam = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueParameter, String>()
		regs.forEachIndexed { idx, pair ->
			val p = pair.second
			val arg = provided[idx]
			val emitted: String? = when {
				arg != null -> argExpr(arg, p)
				else -> {
					val def = p.defaultValue?.expression
					when {
						def == null -> null
						def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression ->
							// CROSS-MODULE: the jar dropped the default VALUE. A data-class `copy` (Pair/Triple, or any referenced
							// data class) is a SPECIAL case: its omitted-field default is ALWAYS `this.<field>` by construction, so
							// reconstruct it as a receiver FIELD read at the INSTANTIATED call site — the exact BIR kotc emits for a
							// plain `pair.first` (owner = the actual `kotlin.Pair[Int,Int]`, so no generic `gp:` token leaks; the
							// @KotlinDefault splice can't carry that instantiation). This is the Pair/Triple partial-`copy` fix (C3).
							if ((callee as? org.jetbrains.kotlin.ir.declarations.IrSimpleFunction)?.let { isDataClassCopy(it) } == true && recvJson != null)
								(dispatchReceiver(call) ?: extensionReceiver(call))?.let { r ->
									// Owner via ownerSpec (the SAME token the plain `pair.first` property read uses — the referenced,
									// instantiated `kotlin.Pair[Int,Int]`, no `@` this-assembly prefix, no open `gp:` param).
									"""{"k":"field","ownerType":${ownerSpec(callee.parent as? IrClass, r.type).toJson()},"recv":${recvJson},"name":${str(p.name.asString())}}"""
								}
							// A @KotlinDefault-carrying callee (any non-constant default — joinToString's CharSequence separators,
							// substringAfter's `= this`, `b = a * 10`) gets a POSITIONAL placeholder for EVERY omitted arg so a later
							// provided arg (the trailing transform lambda) keeps its slot; bir2cir fills each from the ref.dll
							// @KotlinDefault (its `{param n}` tokens → this call's args). A callee with only metadata-representable
							// defaults carries none → drop the (trailing) omit for ilemit's [DefaultParameterValue] backfill.
							else if (carries) defaultArgPlaceholder else null
						refsAny(def, valueSyms) -> {
							// SAME-MODULE default reading another VALUE parameter (`b: Int = a * 10`). Inline with each referenced
							// value param rewritten to THIS call's filled arg for that param — the $default-scope evaluation at the
							// emitted-JSON level (the twin of the `= this` receiver case below, via captureSubst instead of a
							// token replace). Best-effort single-eval: a side-effecting earlier arg read by this default is
							// duplicated (documented edge, same as the receiver case).
							val installed = ArrayList<org.jetbrains.kotlin.ir.declarations.IrValueParameter>()
							for ((vp, js) in filledByParam) { captureSubst[vp] = js; installed.add(vp) }
							val js = recvJson?.let { expr(def).replace(defaultArgThisToken, it) } ?: expr(def)
							installed.forEach { captureSubst.remove(it) }
							js
						}
						refsAny(def, receiverSyms) -> {
							// SAME-MODULE default reading the RECEIVER (`= this` / `this.field`). Inline with `this` rewritten to
							// THIS call's receiver — the $default-scope evaluation, at the emitted-JSON level. Every `this` in the
							// callee's default denotes the callee's receiver, so replacing them ALL with this call's receiver is
							// correct (an inserted `{"k":"this"}` from a `this.foo` receiver then denotes the CALLER's this).
							val r = recvJson
							if (r != null) expr(def).replace(defaultArgThisToken, r) else argExpr(def, p)
						}
						else -> argExpr(def, p)   // constant / global — inline verbatim (unchanged)
					}
				}
			}
			if (emitted != null) { out.add(emitted); filledByParam[p] = emitted }
		}
		return out
	}

	/** The call's regular args IN ORDER, filling an omitted default-arg param with its callee's default-value
	 *  expression. A restored function/ctor carries a real constant default (applyDefaults), so the consumer can omit a
	 *  default arg ANYWHERE — trailing, named-middle (`f(c=9)`), or reordered — and the value is filled here. */
	internal fun filledArgExprs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
		val callee = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction) ?: return emptyList()
		val calleeLocals = callee.parameters.map { it.symbol }.toHashSet()
		val out = ArrayList<IrExpression>()
		callee.parameters.forEachIndexed { i, p ->
			if (p.kind != IrParameterKind.Regular) return@forEachIndexed
			val arg = if (i < call.arguments.size) call.arguments[i] else null
			if (arg != null) out.add(arg)
			else (p.defaultValue?.expression)?.let { def ->
				// A CROSS-MODULE callee's default value does NOT deserialize from the jar/metadata as a real IR expression:
				// the frontend hands back an IrErrorExpression placeholder. Inlining it would reach ilemit as "IrError-
				// Expression has no .NET lowering". Instead, OMIT the (trailing) arg — ilemit's call path then fills it from
				// the callee's .NET [DefaultParameterValue] metadata (EmitCallArgs), which carries the REAL constant default
				// (e.g. Regex.find's startIndex=0). This is the intended "constant default -> metadata -> ilemit fill" path.
				if (def is org.jetbrains.kotlin.ir.expressions.IrErrorExpression) return@let
				// Filling an OMITTED default inlines the callee's default expression at THIS call site — fine for a
				// constant/global, but a default that reads the callee's OWN parameters/receiver (`b: Int = a * 10`, or a
				// data class `copy`'s `x = this.x`) must be evaluated in the callee's scope (cf. Kotlin/JVM's `$default`),
				// which the .NET backend doesn't yet do. Reject only HERE — at the omitting call — not at the declaration:
				// a data class whose `copy` is never arg-omitted must still compile. Otherwise a dangling `local a`/`this`
				// reaches ilemit as invalid IL. See docs/future-work-interop.md (non-constant default arguments).
				if (refsAny(def, calleeLocals)) unsupported(call, "omitting a non-constant default argument",
					"the default value of parameter '${p.name.asString()}' references other parameters or the receiver, " +
					"which the .NET backend cannot evaluate at the call site; pass the argument explicitly")
				out.add(def)
			}
		}
		return out
	}

	/** True if `expr` reads any of `locals` — detects a default-arg expression that references the callee's own
	 *  parameters/receiver (e.g. `b = a * 10`, or a data class `copy`'s `this.x`), which can't be inlined at a call site. */
	internal fun refsAny(expr: IrExpression, locals: Set<org.jetbrains.kotlin.ir.symbols.IrValueSymbol>): Boolean {
		var found = false
		expr.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				if (found) return
				if (element is IrGetValue && element.symbol in locals) { found = true; return }
				element.acceptChildrenVoid(this)
			}
		})
		return found
	}

	internal fun regularArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: emptyList()
		return call.arguments.mapIndexedNotNull { i, a ->
			if (a != null && i < params.size && isValueParameter(params[i])) a else null
		}
	}

	internal fun dispatchReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	/** The callee's ordinary (non-receiver) value parameters, in order. */
	internal fun regularParams(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrValueParameter> =
		callee.parameters.filter { isValueParameter(it) }

	internal fun extensionReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	/** Same index-by-parameter-kind approach as [dispatchReceiver]/[extensionReceiver], for an `IrPropertyReference`
	 *  (which has no callee `IrFunction` of its own — the getter's parameter SHAPE is used to index its `arguments`).
	 *  `IrMemberAccessExpression.dispatchReceiver`/`.extensionReceiver` (the convenience getters) are ERROR-level
	 *  deprecated in this compiler version, so kotc never calls them directly, here or on an `IrCall`. */
	internal fun propRefDispatchReceiver(node: IrPropertyReference): IrExpression? {
		val params = (node.getter?.owner ?: node.setter?.owner)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		return if (idx in 0 until node.arguments.size) node.arguments[idx] else null
	}

	internal fun propRefExtensionReceiver(node: IrPropertyReference): IrExpression? {
		val params = (node.getter?.owner ?: node.setter?.owner)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		return if (idx in 0 until node.arguments.size) node.arguments[idx] else null
	}

	/**
	 * Lift a function-local class to a top-level synthetic type. Referenced outer locals (incl. the enclosing
	 * `this`) become leading ctor params / capture fields; construction sites prepend those values (see the
	 * IrConstructorCall handler). Returns a no-op statement (the declaration emits nothing inline).
	 */
	internal fun liftLocalClass(klass: IrClass): String {
		if (anonNames.containsKey(klass)) return """{"k":"block","body":[]}"""   // already lifted
		val cname = "dotkt\$${klass.name.asString()}\$${scopeCounter++}"
		anonNames[klass] = cname
		val captured = capturedVarsForObject(klass)
		// Writing a captured outer local from the class needs heap ref-cells (same as the object-literal case).
		if (captured.any { it in mutatedIn(klass) && !isRefCell(it) })
			return unsupported(klass, "a local class that writes to a captured outer variable",
				"read-only capture works; pass the value in by constructor, or use a class field")
		// Capturing an enclosing type parameter isn't supported for a local class yet (it would need a generic lift +
		// constructed type uses) — a clear error beats invalid IL. A capturing lambda or local fun does support it.
		if (freeTypeParams(captured.map { it.type }).isNotEmpty())
			return unsupported(klass, "a local class that captures an enclosing generic type parameter",
				"move the logic into a (capturing) lambda or a local fun, which do support it")
		val capPairs = captured.map { it to captureFieldName(it) }
		capPairs.forEach { (decl, fname) ->
			captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		}
		liftedTypes.add(typeDef(klass, capPairs, generated = true))
		capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
		localClassCaptures[klass] = captured
		return """{"k":"block","body":[]}"""
	}

	/**
	 * Bind a subject/receiver expression for exactly-ONCE evaluation before splicing it into several use sites.
	 * A STABLE expression (a const, or a read of an immutable non-ref-cell local/parameter) splices directly —
	 * re-reading it is free and side-effect-free. Anything else gets a temp local (returned as a `var` statement
	 * for a wrapping valueBlock) and the use sites splice the local READ: splicing the rendered initializer JSON
	 * itself re-evaluates it per splice (the when-subject / safe-call / range-membership double-eval defect).
	 * Returns (varStmtJson or null-if-stable, useJson). Only safe where the expression is suspension-free —
	 * expression position is; a suspend call there just renders plainly with `"suspendCall":true` for bir2cir.
	 */
	internal fun bindOnce(init: IrExpression, type: IrType, prefix: String): Pair<String?, String> {
		val stable = init is IrConst || (init as? IrGetValue)?.symbol?.owner?.let { o ->
			!isRefCell(o) && (o is IrValueParameter || (o as? IrVariable)?.isVar == false)
		} == true
		if (stable) return null to expr(init)
		val tv = "$prefix${scopeCounter++}"
		// A NULLABLE generic-param subject (`T?`, e.g. `x as? T`) must NOT become a `gp:T` local: `!T` cannot
		// hold null when T is instantiated with a value type, and the `isinst` REF result stored into a `!T`
		// slot is unverifiable ([found ref 'T'][expected value 'T'] — the stdlib's documented "never hold a V?
		// in a local" rule, ClrMapDefaults.kt). Erase to object: every use site of a nullable subject is
		// ref-typed (objEq null-check / objMethod / ref member).
		val bt = birType(type)
		// A nullable generic-param subject now surfaces as `{t:nullable,of:tv}` (uniform birType) — erase THAT to object.
		val vt = if (bt is TypeNode.Nullable && bt.of is TypeNode.Tv) OBJ else bt
		return """{"k":"var","name":${str(tv)},"type":${vt.toJson()},"init":${expr(init)}}""" to
			"""{"k":"local","name":${str(tv)}}"""
	}

	internal fun blockExpr(block: IrBlock): String {
		// `object : I { … }` -> a synthetic named class (lifted) + `new`. Instance fields are real fields;
		// captured outer values (incl. the enclosing `this`) become extra ctor params / capture fields.
		if (block.origin?.toString() == "OBJECT_LITERAL") {
			val anon = block.statements.filterIsInstance<IrClass>().firstOrNull()
			if (anon != null) {
				val cname = "dotkt\$obj${scopeCounter++}"
				anonNames[anon] = cname
				val captured = capturedVarsForObject(anon)
				// Mutable capture (writing an outer local through the object) would need heap ref-cells.
				if (captured.any { it in mutatedIn(anon) && !isRefCell(it) })
					return unsupported(block, "an object expression that writes to a captured outer variable",
						"read-only capture works; to mutate shared state, use a small class with a field instead")
				// Capturing an ENCLOSING TYPE PARAMETER (`fun <T> mk(v:T) = object : Box<T> { ... }`, or an inlined object
				// whose supertype/captures resolve to the enclosing `T`): typeDef makes the synthesized class GENERIC over
				// the params its members reference (reified CLR generics), recording them in `liftedTypeArgNames`. The `new`
				// site must then INSTANTIATE it with the enclosing args — bracket those `gp:` tokens onto the constructed type
				// (they resolve at THIS site, i.e. the enclosing method/type scope). Mirrors newClosure/newSam's `typeArgs`.
				val capPairs = captured.map { it to captureFieldName(it) }
				// Save any PRIOR binding for each captured decl: when this object literal is nested inside a capturing
				// closure/object that captures the SAME outer var (`element`), the enclosing frame already bound it to
				// its OWN field. Blindly `remove`ing after typeDef would clobber that, so the capture VALUE below would
				// mis-render as a bare `local element` (the enclosing `this.element` is out of scope at the `new` site ->
				// ilemit "load unknown var"). Restore the prior binding instead — mirrors the closure path (lambda()).
				val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
				capPairs.forEach { (decl, fname) ->
					captureSubst[decl] = """{"k":"field","ownerType":${fqnJson(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
				}
				liftedTypes.add(typeDef(anon, capPairs, liftedAnon = true, generated = true))
				capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
				// Instantiate the flattened generic anon with the captured params rendered in THIS (enclosing) scope:
				// birType honors any active inline `typeArgSubst` and otherwise yields the enclosing `tv` (method/type).
				val tpParams = liftedTypeArgParams[anon].orEmpty()
				// Capture values are evaluated in the OUTER context (this frame's captureSubst restored above).
				val capArgs = captured.joinToString(",") { capValueExpr(it) }
				val newType = if (tpParams.isEmpty()) TypeNode.Fqn(cname)
					else TypeNode.Fqn(cname, tpParams.map { typeArgSubst[it] ?: tvOf(it) })
				return """{"k":"new","type":${newType.toJson()},"args":[$capArgs]}"""
			}
		}
		// `when (subject)` lowers to `{ val tmp = subject; WHEN }` in expression position. The subject is bound
		// ONCE (bindOnce): the old code stored valSubst[key] = the RENDERED initializer JSON, so every IrGetValue
		// of the subject re-spliced — and re-EVALUATED — it (a when-subject call ran once per branch test; a
		// safe-call receiver ran twice).
		val tmp = block.statements.getOrNull(0) as? IrVariable
		val whenExpr = block.statements.getOrNull(1) as? IrWhen
		if (block.statements.size == 2 && tmp != null && whenExpr != null && tmp.initializer != null) {
			val key = tmp.name.asString()
			val origin = block.origin?.toString()
			// Save/restore (not remove) the key: a nested same-named subject must not clobber the outer splice.
			val saved = valSubst[key]
			fun restore() { if (saved != null) valSubst[key] = saved else valSubst.remove(key); valSubstUnwrapped.remove(key) }
			// `a?.member` where member is a value type -> Nullable<T>: bind `a` once, then null-gate. A nullable
			// VALUE-type receiver (`Char?`) is gated by HasValue and the member sees the UNWRAPPED .Value (the
			// ELVIS shape below) — splicing the raw Nullable<T> where the element is required emitted invalid IL
			// (e.g. `conv int` over a Nullable<char> -> InvalidProgramException).
			if (origin == "SAFE_CALL") nullableElem(block.type)?.let { elem ->
				val (subjVar, subj) = bindOnce(tmp.initializer!!, tmp.type, "__nv")
				val recvElem = nullableElem(tmp.type)
				val core: String
				if (recvElem != null) {
					valSubst[key] = """{"k":"nullableValue","elem":${str(recvElem)},"e":$subj}"""
					valSubstUnwrapped.add(key)   // receiver already reads .Value -> the value-nullable unwrap helpers must not re-wrap
					val member = expr(whenExpr.branches.last().result)
					core = """{"k":"cond","cond":{"k":"nullableHasValue","elem":${str(recvElem)},"e":$subj},"then":{"k":"nullableWrap","elem":${str(elem)},"e":$member},"else":{"k":"nullableNull","elem":${str(elem)}}}"""
				} else {
					valSubst[key] = subj
					val nullCheck = expr(whenExpr.branches.first().condition)
					val member = expr(whenExpr.branches.last().result)
					core = """{"k":"cond","cond":$nullCheck,"then":{"k":"nullableNull","elem":${str(elem)}},"else":{"k":"nullableWrap","elem":${str(elem)},"e":$member}}"""
				}
				restore()
				return if (subjVar == null) core
				else """{"k":"valueBlock","type":${birType(block.type).toJson()},"stmts":[$subjVar],"result":$core}"""
			}
			// `nv ?: d` where nv is a Nullable<T> -> evaluate once, then HasValue ? Value : d.
			if (origin == "ELVIS") nullableElem(tmp.type)?.let { elem ->
				val nv = "__nv${scopeCounter++}"
				val init = expr(tmp.initializer!!)
				// ELVIS lowers to `when { tmp == null -> fallback; else -> tmp }`:
				// branches[0].result is the fallback; branches.last() is tmp (ignored — we read .Value).
				val elseResult = expr(whenExpr.branches.first().result)
				val nvLoc = """{"k":"local","name":${str(nv)}}"""
				return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(elem).toJson()},"init":$init}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${elem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${elem.toJson()},"e":$nvLoc},"else":$elseResult}}"""
			}
			val (subjVar, subj) = bindOnce(tmp.initializer!!, tmp.type, "__subj")
			valSubst[key] = subj
			val result = ternary(whenExpr)
			restore()
			// The wrapping valueBlock carries the when's result type: the old bare `cond` surfaced it (ternary's
			// "type"), and bir2cir's call-arg type inference sniffs the argument node's type field.
			return if (subjVar == null) result
			else """{"k":"valueBlock","type":${birType(whenExpr.type).toJson()},"stmts":[$subjVar],"result":$result}"""
		}
		// A general block in value position: emit its preceding (side-effecting) statements, then the last value.
		// e.g. `{ counter++ }` lowers to `{ val <unary> = counter; counter = counter + 1; <unary> }` — dropping the
		// leading statements would lose the temp + the assignment.
		val last = block.statements.lastOrNull()
		if (block.statements.size > 1 && last is IrExpression) {
			val pre = block.statements.dropLast(1).joinToString(",") { stmt(it) }
			return """{"k":"valueBlock","stmts":[$pre],"result":${expr(last)}}"""
		}
		return (last as? IrExpression)?.let { expr(it) } ?: """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
	}

	internal fun ternary(node: IrWhen): String {
		// Fold right-to-left into nested conditionals. The branches carry the when's result type, so a value-type
		// nullable result (`Int?`) gets its `T`/`null` branches coerced to Nullable<T> at emit (see EmitCond).
		// GOTCHA: an inlined `takeIf` etc. yields `if (c) x else null` whose joined type is a value primitive with
		// a bare `null` branch — but the emitted cond type comes out non-nullable (`kotlin.Int`). Two shapes reach
		// here: (1) the FIR `.type` is the non-null `Int` (the `T?` rides the fn return), or (2) `takeIf`'s generic
		// `T?` result, where `birType` substitutes `T -> kotlin.Int` and DROPS the `?`. In both, tag the cond
		// `nullable:<elem>` so ilemit joins the value branch (wrap to Nullable<T>) and the null branch (HasValue=false);
		// leaving it `int` mismatches `then:int` vs `else:null-ref`. The `null` may arrive IR-wrapped (IMPLICIT_CAST /
		// inline block — as from `takeIf`), so detect it on the EMITTED result (a bare `const … null`), emitting each
		// branch result exactly once. A reference-typed join with a null branch keeps its type (null is a valid ref).
		val branches = node.branches.map { b -> Triple((b.condition as? IrConst)?.value == true, b.condition, expr(b.result)) }
		val nullBranch = branches.any { isEmittedNullConst(it.third) }
		val bt = birType(node.type)
		val elem: TypeNode? = if (bt is TypeNode.Nullable) null else (bt as? TypeNode.Fqn)?.takeIf { node.type.isValuePrimitive() }
		val ty = (if (nullBranch && elem != null) TypeNode.Nullable(elem) else bt).toJson()
		var acc = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
		for ((isElse, cond, result) in branches.asReversed()) {
			acc = if (isElse) result
			else """{"k":"cond","type":$ty,"cond":${expr(cond)},"then":$result,"else":$acc}"""
		}
		return acc
	}

	/** True if an EMITTED BIR expression is a bare `null` const — `{"k":"const",…,"value":null}` (a
	 *  `void`/`kotlin.Nothing`-typed null). Used to spot a `when`/`if` branch that yields `null`. */
	internal fun isEmittedNullConst(emitted: String): Boolean =
		emitted.startsWith("""{"k":"const",""") && emitted.endsWith(""","value":null}""")

	internal fun call(call: IrCall): String {
		// A `tailrec` self-tail-call -> a back-jump to the method entry (TCO, §2b) instead of a recursive call. Matched
		// by IR identity against the frontend-validated tail-call set installed by `method()`.
		tailrecCtx?.let { ctx -> if (call in ctx.calls) return tailrecJump(call, ctx) }
		val callee = call.symbol.owner
		// NOTE: kotlin.text.MatchResult.value is a REAL interface property (realized by ClrMatchResult) — it must route
		// through the ordinary member-call path, NOT a hardcoded System...Match.Value lowering (that leftover forced the
		// broken MatchResult->Match aliasing above and mis-typed the call).
		// `.message`/`.cause` on a Throwable subclass is a PLAIN Kotlin property read: kotc emits the ordinary
		// `callInstance get_message`/`get_cause` (with its `overrides` chain to kotlin.Throwable) below, and bir2cir
		// substitutes it to `clrPropGet System.Exception.Message`/`.InnerException` off the @ClrProperty binding on the
		// ref.dll (kotlin.Throwable is @ClrTypeAlias("System.Exception")). No BCL member name lives in kotc (layer purity).
		// `kotlin.sequences.sequence { yield(…) }` is now ORDINARY library code: it resolves to the real stdlib
		// `sequence(block)` function over the cold core (SequenceBuilderIterator), with `{ yield(...) }` flowing through
		// the ordinary suspend-lambda path (newSuspendLambda -> bir2cir's RestrictedSuspendLambda SM). kotc has NO
		// knowledge of the `sequence`/`yield`/`yieldAll` symbols — the compiler no longer knows the builder exists.
		// `stackBuffer(n) { … }` intrinsic -> scoped stack allocation (splice the block into the caller's frame).
		// Matched by FULL name (`kotlin.clr.stackBuffer`, its CLR-intrinsic home) so a user function happening to be
		// named `stackBuffer` is not mistaken for the intrinsic.
		if (callee.fqNameWhenAvailable?.asString() == "kotlin.clr.stackBuffer")
			return emitStackBuffer(call)
		// A .NET event subscription `w.Changed += h` / `-= h` resolves (normal Kotlin operator resolution) to the
		// `plusAssign`/`minusAssign` member operator of the injected `kotlin.clr.ClrEvent<T>` fiction (the surfaced
		// form of a .NET event member — see ClrTypeInjection). kotc emits the PLAIN Kotlin operator-call identity: a
		// `callInstance` on `kotlin.clr.ClrEvent` whose receiver is the event member-access `w.Changed` (a clrPropGet
		// carrying the .NET owner type + event name). NO `add_`/`remove_` naming, NO clrEventAdd here — bir2cir's
		// ClrEventOperatorBinding recognizes this node and binds it to the .NET add/remove accessor (the Kotlin<->CLR
		// event relation lives in bir2cir, not kotc). The ClrEvent<T> value is never materialized.
		if ((callee.name.asString() == "plusAssign" || callee.name.asString() == "minusAssign")
			&& (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString() == "kotlin.clr.ClrEvent") {
			val recv = dispatchReceiver(call)!!
			// The receiver here is the ONLY legitimate ClrEvent-value position (the event member-access `w.Changed`);
			// emit it with the OK flag so its clrPropGet is allowed. Every other ClrEvent read stays a compile error.
			val recvJson = asClrEventReceiver { expr(recv) }
			return """{"k":"callInstance","ownerType":${fqnJson("kotlin.clr.ClrEvent")},"virtual":false,"recv":$recvJson,"method":${str(callee.name.asString())},"args":[${expr(regularArgs(call).first())}]}"""
		}
		// A `StackBuffer<T>` member access while its block is being spliced -> a stack op (ptr + index).
		((dispatchReceiver(call) as? IrGetValue)?.symbol?.owner)?.let { stackBufSubst[it] }?.let { return emitStackBufferOp(call, callee, it) }
		// A `<get-x>`/`<set-x>` call for a LOCAL delegated property -> access on the delegate local (thisRef=null,
		// no enclosing instance). `by lazy`: the local's `.Value`; custom delegate: getValue/setValue(null, KProperty).
		localDelegates[callee]?.let { ldp ->
			val dvar = ldp.delegate
			val dlocal = """{"k":"local","name":${str(dvar.name.asString())}}"""
			val elem = birType(ldp.getter.returnType)
			// A `ClrRef<T>` delegate (byref local): getValue/setValue inline to ldobj/stobj through the managed pointer.
			if (birType(dvar.type) is TypeNode.ByRef)
				return if (callee === ldp.setter)
					"""{"k":"byrefStore","local":${str(dvar.name.asString())},"elem":${str(elem)},"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"byrefLoad","local":${str(dvar.name.asString())},"elem":${str(elem)}}"""
			// `by lazy` (local): the delegate is a real `kotlin.Lazy<T>` (the stdlib `UnsafeLazyImpl`). Its accessor is
			// the InlineOnly `Lazy<T>.getValue(…) = value` operator, whose stdlib inline body is absent from our IR;
			// inline it (a pure Kotlin-frontend fact) to a plain read of the Lazy interface's `value` getter. bir2cir/
			// ilemit resolve the real emitted `kotlin.Lazy::get_value` — no CLR (System.Lazy) knowledge in kotc.
			if (dvar.type.classFqName?.asString() == "kotlin.Lazy" && callee === ldp.getter) {
				val owner = ownerSpec(dvar.type.classifierOrNull?.owner as? IrClass, dvar.type)
				return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$dlocal,"method":"get_value","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, ldp.getter.returnType)}}"""
			}
			val delegateClass = dvar.type.classifierOrNull?.owner as? IrClass
			val dvFq = dvar.type.classFqName?.asString()
			// A user delegate class -> its concrete type; a stdlib Read(Write)Property-typed delegate (e.g.
			// `by Delegates.observable(…)`) -> the REAL generic stdlib interface (mirrors `by lazy` on real
			// `kotlin.Lazy<T>`), binding to the actual emitted stdlib getValue/setValue.
			val (owner, ownerGeneric) = when {
				delegateClass != null && clrName(delegateClass) == null &&
					delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true -> str(typeName(delegateClass)) to false
				dvFq == "kotlin.properties.ReadWriteProperty" || dvFq == "kotlin.properties.ReadOnlyProperty" -> {
					val os = ownerSpec(delegateClass, dvar.type)
					os.toJson() to ((os as? TypeNode.Fqn)?.args != null)
				}
				else -> null to false
			}
			if (owner != null) {
				val kprop = kPropertyStub(ldp.name.asString())
				val nullRef = """{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}"""
				return if (callee === ldp.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"setValue","args":[$nullRef,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$dlocal,"method":"getValue","args":[$nullRef,$kprop]${retHint(ownerGeneric, ldp.getter.returnType)}}"""
			}
		}
		val name = callee.name.asString()
		val declaringClass = callee.parent as? IrClass
		// A top-level fn has no declaringClass; fall back to the callee's OWN package so an injected/user top-level
		// operator (e.g. a restored `operator fun Vec.plus`) isn't mistaken for a kotlin builtin and lowered to a `bin`.
		val isBuiltin = (declaringClass?.fqNameWhenAvailable?.asString() ?: callee.fqNameWhenAvailable?.asString())?.startsWith("kotlin") ?: true
		val pkgFqName = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
		val calleeFq = if (declaringClass == null && pkgFqName != null) "$pkgFqName.$name" else null
		
		// A top-level fun annotated @ClrIntrinsic is NOT bound to a STATIC/INSTANCE .NET call here: that
		// @ClrIntrinsic-driven member-call SUBSTITUTION belongs to bir2cir (sourced from the ref.dll), NOT kotc.
		// kotc emits the PLAIN Kotlin top-level call (the clrStatic file-class path below for injected .NET top-level
		// funs is metadata-driven and stays). See [clrName] / CLAUDE.md "kotc reads
		// NEITHER @ClrIntrinsic NOR @ClrTypeAlias".

		// NOTE: collection-interface member routing — `iterator()`/`isEmpty`/`contains`/`containsAll`/`indexOf`/
		// `lastIndexOf`/`subList`/`listIterator()` on a @ClrTypeAlias `kotlin.collections` interface, whose substituted
		// BCL IReadOnly*/IEnumerable face lacks these slots — is OWNED BY bir2cir Rule 5 (Program.cs ~4979), which routes
		// them to the rt `ClrIteratorBridge`/`ClrCollectionDefaults` helpers off the ref.dll @ClrTypeAlias metadata. kotc
		// emits the PLAIN member call (faithful IR); it does NOT name the helper class.

		// A call to a lifted local function -> static call with captured values (incl. enclosing `this`) prepended.
		localFns[callee]?.let { (lname, caps, tps) ->
			val capArgs = caps.map { capValueExpr(it) }
			// If the lifted method is generic (captured enclosing type params), pass them as type arguments.
			val typeArgs = if (tps.isEmpty()) "" else ""","typeArgs":[${tps.joinToString(",") { tvOf(it).toJson() }}]"""
			return """{"k":"callStatic","owner":null,"method":${str(lname)},"args":[${(capArgs + filledArgs(call)).joinToString(",")}]$typeArgs}"""
		}

		// Inlining (lambda-param inline funs only; lambda-less inline = JIT's job — see [[clr-not-jvm-discard-jvmisms]]).
		// (1) An invoke on an inlined lambda param (`action(x)` inside the spliced inline-fun body) -> splice the
		//     lambda body in place. A non-local `return` inside it targets the enclosing fun (already so in the IR),
		//     and valueBlock is emitted INLINE (not an IIFE), so it returns from the caller = correct non-local return.
		if (name == "invoke") (dispatchReceiver(call) as? IrGetValue)?.symbol?.owner?.let { recv ->
			inlineLambdas[recv]?.let { return spliceLambdaCall(it, call) }
		}
		// `(::x)()` (invoking a property-reference VALUE inline) needs NO special handling here: KProperty0/
		// KProperty1's declared `() -> V`/`(T) -> V` supertype gives them a REAL fake-overridden `invoke` abstract
		// member declared directly ON the interface itself (confirmed in the compiled BIR — typeDef's own
		// interfaces-collection drops the FunctionN supertype off ANY interface def, `bt is TypeNode.Fn -> null`,
		// but the fake override still lands in the interface's OWN `methods`). So a call's resolved `declaringClass`
		// for `invoke` on a KProperty0/1-typed receiver is KProperty0/1 itself, never Function0/1 — the ordinary
		// member-call path below emits a plain `callInstance ownerType:kotlin.reflect.KProperty0/1[…] method:invoke`,
		// which `propertyRef`'s lifted class implements directly (mirrors JVM's `PropertyReferenceImpl.invoke() =
		// get()`). bir2cir's CharCodeInvokeLowering only rewrites an `ownerType:kotlin.Function.../KFunction...`
		// call, so it never touches this one.
		// (2) A call to a USER `inline fun` that takes a lambda arg -> splice its body (real inlining). stdlib inline
		//     bodies are absent from our IR, so only user inline funs (body present) inline; others fall through.
		if (callee.isInline && callee.body != null && hasLambdaArg(call)) return inlineCall(call)

		// `Delegates.observable/vetoable/notNull(…)` is NOT intercepted: it resolves to the REAL stdlib
		// `Delegates.observable`/`vetoable`/`notNull` (emitted into DotKt.Stdlib.dll — each returns a real
		// `ReadWriteProperty<Any?,V>`: an `ObservableProperty` subclass or `NotNullVar`) and flows through the
		// ordinary top-level-call path. The delegate-access sites dispatch getValue/setValue on the real generic
		// interface (see the `by lazy`-parallel routing above). No compiler-synthesized delegate class.
		// `by lazy { … }` is NOT intercepted: the `kotlin.lazy(initializer)` call resolves to the real stdlib
		// `lazy()` actual (returns `UnsafeLazyImpl(initializer)`, a pure-Kotlin `Lazy<T>`) and flows through the
		// ordinary top-level-call path below. No System.Lazy construction here (that is CLR knowledge; layer purity).

		if (name == "compareTo") {
			val recv = dispatchReceiver(call)
			val arg = regularArgs(call).firstOrNull()
			val ec = recv?.type?.classifierOrNull?.owner as? IrClass
			if (recv != null && arg != null && ec?.kind == ClassKind.ENUM_CLASS) {
				fun ord(e: IrExpression): String = if (isRichEnum(ec))
					"""{"k":"field","ownerType":${fqnJson(typeName(ec))},"recv":${expr(e)},"name":"__ordinal"}"""
				else """{"k":"enumOrdinal","e":${expr(e)}}"""
				return """{"k":"binOp","op":"-","lhs":${ord(recv)},"rhs":${ord(arg)}}"""
			}
			// A DIRECT primitive `Double/Float.compareTo(y)` is not special-cased here (Kotlin's TOTAL
			// order — `-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN) == 0` — differs from System.Double.CompareTo). kotc
			// emits the FAITHFUL member call (falls through to the plain callInstance path -> `kotlin.Double.compareTo`)
			// and bir2cir recognizes the Double/Float owner and routes to the stdlib clrDoubleCompare/clrFloatCompare
			// total-order body BEFORE its primitive-compareTo -> System.Double.CompareTo routing. The ENUM branch stays.
		}
		// A PRIMITIVE `x.compareTo(y)` and a `kotlin.Comparable.compareTo` (the `<`/`>`/`<=`/`>=` desugaring on a
		// bounded generic `<T : Comparable<T>>`) are not intercepted here (layer purity): kotc emits the PLAIN
		// member call (`callInstance kotlin.Int.compareTo` / `callInstance kotlin.Comparable.compareTo`, carrying the
		// receiver's static type on the recv node's `retType`/`elem` and the type-param constraints). bir2cir derives the
		// CLR form — a primitive owner -> `clrInstance System.<Prim>.CompareTo`; a @ClrTypeAlias("System.IComparable")
		// owner -> a `constrained.` `System.IComparable<T>::CompareTo` (its ComparableConstrain pass, reusing the
		// value-type/constrained-dispatch knowledge it already owns). The `System.IComparable`/`constrained.` decision
		// is a Kotlin<->CLR relation and lives in bir2cir, not this frontend.

		// NOTE: `reified` gets NO special handling here — it is deliberately never inspected. The CLR has reified
		// generics, so `reified` is pure decoration: a generic function (reified or not) is just emitted as a .NET
		// generic method, and a body that uses `T::class`/`x is T`/`x as T` lowers to `ldtoken !!0`/`isinst !!0`
		// like any other generic-method body. (On the JVM `reified` exists ONLY to drive call-site inlining around
		// erasure; that whole machine is absent here.) See [[clr-not-jvm-discard-jvmisms]].

		// `T::class.simpleName`/`.qualifiedName` is NOT intercepted here (layer purity): kotc emits the PLAIN Kotlin
		// property read `kotlin.reflect.KClass::get_simpleName`/`get_qualifiedName` (via the ordinary member-property
		// path below), and bir2cir's KClassMemberBinding derives the CLR resolution — a `clrPropGet` on `System.Type`
		// (`Name`/`FullName`). The `System.Type` knowledge (which BCL member a KClass member maps to) is a Kotlin<->CLR
		// relation and lives in bir2cir, not in this frontend.

		// Scope functions (let/run/with/apply/also) -> inline to a value-block (no delegate; mirrors the C# IIFE).
		if (calleeFq in SCOPE_FUNCTIONS) {
			val isWith = calleeFq == "kotlin.with"
			val recvExpr = if (isWith) regularArgs(call).getOrNull(0) else extensionReceiver(call)
			val lambda = (if (isWith) regularArgs(call).getOrNull(1) else regularArgs(call).getOrNull(0)) as? IrFunctionExpression
			if (recvExpr != null && lambda != null) return inlineScope(calleeFq!!, recvExpr, lambda)
		}
		// `r.use { block }` (Closeable/AutoCloseable) -> `try { block(r) } finally { r.close()/Dispose() }`, returning
		// the block's value. The CLR analogue of C# `using` (close -> IDisposable.Dispose). T : (Auto)Closeable.
		if (calleeFq == "kotlin.io.use" || calleeFq == "kotlin.use") {
			val recvExpr = extensionReceiver(call)
			val lambda = regularArgs(call).getOrNull(0) as? IrFunctionExpression
			if (recvExpr != null && lambda != null) return inlineUse(recvExpr, lambda, birType(call.type))
		}

		// Collection/array factories (`listOf`/`setOf`/`mapOf`/`arrayOf`/`intArrayOf`/`arrayOfNulls`/…) are not
		// recognized here: kotc emits the plain top-level `callStatic kotlin.collections.listOf(...)` (the faithful IR;
		// the vararg argument itself rides as a `newArray` node). bir2cir reads the `@kotlin.clr.ClrCollectionFactory`
		// (kind list/set/map) / `@kotlin.clr.ClrArrayFactory` (vararg/sized) marker off each stdlib factory function on
		// the ref.dll and re-emits the same `{k:newList/newSet/newMap/newArray/newArraySized}` construction node — the
		// element/key/value types from the call's `typeArgs`, the elements from the vararg arg. The `mapOf(a to b)`
		// literal-split (and its "do NOT force-split a non-literal Pair" guard — `mapOf(pairVar)` stays a real call)
		// is bir2cir's.

		// Unsigned<->signed byte-array reinterpret (#76) — `UByteArray.toByteArray()` / `ByteArray.toUByteArray()` — is
		// NOT lowered here: it is a CLR-representation fact ("UByteArray IS byte[]"), so kotc emits the FAITHFUL top-level
		// extension call and bir2cir re-emits the reinterpret `cast` keyed on the resolved receiver identity
		// (FaithfulHintRecognition, M9). The Kotlin<->CLR relation lives there, not in kotc.

		// `e!!` (not-null assertion). A value-type-nullable operand (`Int?` = `Nullable<T>`) MUST unwrap via
		// HasValue/Value and throw NPE on null: a bare pass-through leaves a `Nullable<T>` STRUCT where the use
		// site consumes the bare value (`n!! + 1` -> InvalidProgram; `n!!.toLong()` reads garbage) AND silently
		// skips the null check (`z!!` never throws). Mirrors the requireNotNull/checkNotNull value-nullable path
		// below, but `!!` throws kotlin.NullPointerException. Reference-nullable stays a pass-through.
		if (name == "CHECK_NOT_NULL") {
			val arg = call.arguments.filterNotNull().first()
			val velem = nullableElem(arg.type)
			if (velem != null) {
				val nv = "__nn${scopeCounter++}"
				val nvLoc = """{"k":"local","name":${str(nv)}}"""
				return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(velem).toJson()},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${velem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${velem.toJson()},"e":$nvLoc},"else":${throwExpr(newExc("kotlin.NullPointerException", null))}}}"""
			}
			return expr(arg)
		}

		// Value-position primitive `rangeTo`/`rangeUntil` (`a..b` / `a..<b`) is NOT lowered here. kotc emits the
		// FAITHFUL `callInstance kotlin.Int.rangeTo(b)` member call (CLR primitives have no instance methods, but that
		// is a CLR fact); bir2cir (RangeConstructionLowering) MATERIALIZES the stdlib range class — `new IntRange/
		// LongRange/CharRange`, applying the `-1` half-open arithmetic for rangeUntil. Structured for-loops are still
		// counter-lowered in birForLoop (they intercept the range at the IR level before this member call is emitted).

		// `x in a..b` (range membership) -> `(x >= a && x <op> b)` via a short-circuit cond. `x` is bound ONCE
		// (bindOnce): rendering it into both comparison legs re-evaluated a side-effecting `x` twice.
		if (name == "contains") {
			val range = dispatchReceiver(call) as? IrCall
			val value = regularArgs(call).firstOrNull()
			if (range != null && value != null) {
				val ops = range.arguments.filterNotNull()
				val cmp = when (range.symbol.owner.name.asString()) { "rangeTo" -> "<="; "until", "rangeUntil" -> "<"; else -> null }
				if (cmp != null && ops.size == 2) {
					val (xVar, x) = bindOnce(value, value.type, "__in")
					val lo = expr(ops[0]); val hi = expr(ops[1])
					val core = """{"k":"cond","cond":{"k":"binOp","op":">=","lhs":$x,"rhs":$lo},"then":{"k":"binOp","op":${str(cmp)},"lhs":$x,"rhs":$hi},"else":{"k":"const","type":${fqnJson("kotlin.Boolean")},"value":false}}"""
					return if (xVar == null) core else """{"k":"valueBlock","stmts":[$xVar],"result":$core}"""
				}
			}
		}

		// Enum rich API: Color.values()/entries -> Enum.GetValues<T>(); Color.valueOf(s) -> Enum.Parse<T>(s).
		(callee.parent as? IrClass)?.takeIf { it.kind == ClassKind.ENUM_CLASS }?.let { ec ->
			val et = "@" + ec.name.asString()
			// Rich enum -> the synthesized static values()/valueOf() methods on the class.
			if (isRichEnum(ec)) {
				if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
					return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"values","args":[]}"""
				if (name == "valueOf") return """{"k":"callStatic","owner":${fqnJson(ec.name.asString())},"method":"valueOf","args":[${expr(regularArgs(call).first())}]}"""
			}
			if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
				return """{"k":"enumValues","type":${str(et)}}"""
			if (name == "valueOf") return """{"k":"enumParse","type":${str(et)},"arg":${expr(regularArgs(call).first())}}"""
		}
		// The top-level reified enum intrinsics `enumValues<T>()` / `enumValueOf<T>(name)` / `enumEntries<T>()`
		// / `enumEntriesIntrinsic<T>()` are NOT recognized here: kotc emits the FAITHFUL top-level call
		// `callStatic owner:null method:<the callee's bare name> typeArgs:[T] args:[…]` (the plain Kotlin fact) via the
		// general call path. bir2cir's EnumIntrinsicLowering re-emits the same BIR vocabulary — a rich enum's synthesized static
		// `values()`/`valueOf()`, or the semantic `enumValues`/`enumParse` node for a basic/generic-param T — deriving
		// rich-vs-basic from the enum type's emitted shape (a local rich enum carries `enumRich:true`). "This call is
		// enumValues" is a Kotlin<->CLR relation, so it lives in bir2cir. (The `.name`/`.ordinal` handling below asks
		// the IR — `ClassKind.ENUM_CLASS` — not an FQN table, so it stays here.)
		// `c.code` (Char -> Int code point) is NOT recognized here: kotc emits the FAITHFUL top-level extension-property
		// getter call `callStatic owner:null method:get_code sig:[kotlin.Char] args:[<char>]` (the plain Kotlin fact) via
		// the general property path. bir2cir's CharCodeInvokeLowering re-emits the `{k:conv, to:kotlin.Int}` node (a
		// genuine primitive IL op — the char value AS an int, distinct from `.toInt()`'s @ClrConv) off that faithful
		// call, resolving `get_code`->kotlin.CharCodeKt against the ref.dll. The Kotlin<->CLR relation lives in bir2cir.
		// c.name -> ToString() (enum name); c.ordinal -> (int)c.  Rich enum -> the __name/__ordinal fields.
		dispatchReceiver(call)?.takeIf { (it.type.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS }?.let { rc ->
			val rec = (rc.type.classifierOrNull?.owner as? IrClass)
			if (rec != null && isRichEnum(rec)) when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
				"name" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__name"}"""
				"ordinal" -> return """{"k":"field","ownerType":${fqnJson(rec.name.asString())},"recv":${expr(rc)},"name":"__ordinal"}"""
			}
			when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
				"name" -> return """{"k":"objMethod","method":"ToString","recv":${expr(rc)}}"""
				"ordinal" -> return """{"k":"enumOrdinal","e":${expr(rc)}}"""
			}
		}

		// `a to b` and Pair/Triple/IndexedValue `componentN()` are NOT recognized here: these are real
		// emitted stdlib types with real members — the infix `to` (body `Pair(this, that)`) and the data-class
		// component1()/component2()/component3() operators are materialized IR declarations. kotc emits the plain call
		// (faithful IR) and it resolves against the real stdlib surface; no marker is needed (unlike conv/factories,
		// which synthesize CLR-shaped nodes). So `5 to 6`, `val (a, b) = pair`, `t.component1()` all fall through to
		// the ordinary call path.
		// Map-entry destructuring `entry.component1()/.component2()` is NOT lowered to KeyValuePair.Key/.Value here:
		// map entries are real `kotlin.collections.Map.Entry` objects (rt ClrMutableMapEntry; both Map/MutableMap alias
		// IDictionary), so the destructure components emit as the PLAIN Kotlin extension calls and resolve like any
		// stdlib call. Reading a ref object as a KeyValuePair struct would reinterpret memory -> garbage values (and
		// KeyValuePair is CLR knowledge the layer rules forbid inside kotc).

		// Invoking a function-typed value `f(x)` -> delegate `Invoke` (Func/Action) is NOT recognized here: kotc emits
		// the FAITHFUL `callInstance ownerType:kotlin.FunctionN[..]/kotlin.reflect.KFunctionN[..] method:invoke` member
		// call (the plain Kotlin fact) via the general instance-call path. bir2cir's CharCodeInvokeLowering re-emits the
		// `{k:delegateInvoke}` node off that faithful call — deriving `funcType` from the FunctionN owner's type args
		// (params = args[..n-1], ret = args[n]). A function-typed value IS a delegate at the CLR level; that Kotlin<->CLR
		// relation lives in bir2cir. (Includes a callable-reference value `(c::method)(x)` whose type is `KFunctionN`.)
		// MutableList/MutableCollection mutation members (`add`/`remove`/`clear`/`removeAt`) -> the BCL List<T>
		// instance method. Kotlin collections lower to System.Collections.Generic.List<T>; these are instance calls,
		// not collection extension ops (the real stdlib `map`/`filter`/`mapTo` bodies — which build an ArrayList via
		// `.add(...)` — run on the BCL list).
		// Array indexing `a[i]` / `a[i] = v` (the `get`/`set` operators on Array/primitive arrays).
		if (callee.isOperator && (name == "get" || name == "set")) {
			val recv = dispatchReceiver(call)
			if (recv != null && isArrayType(recv.type)) {
				// No `elem` field: bir2cir DERIVES the element off the array operand's (now faithful) type. kotc emits
				// only the faithful get/set intrinsic + the array operand.
				val a = regularArgs(call)
				return if (name == "get") """{"k":"arrayGet","array":${expr(recv)},"index":${expr(a[0])}}"""
				else """{"k":"arraySet","array":${expr(recv)},"index":${expr(a[0])},"value":${expr(a[1])}}"""
			}
			// String indexing `s[i]` is NOT lowered here: `kotlin.String.get(index)`
			// carries @ClrIntrinsic("get_Chars") (runtime/stdlib/clr/builtins/String.kt); kotc emits the plain operator
			// `get` member call on kotlin.String and bir2cir's MemberCallSubstitution rewrites it to
			// `clrInstance System.String.get_Chars` off the ref.dll — the Kotlin<->CLR relation lives in bir2cir, not kotc.
			// kotlin.* List/Map indexing `list[i]`/`m[k]` is NOT intercepted: in FIR it's already an operator call to
			// `get`/`set` — fall through to the ordinary call path so it emits as a real kotlin.* `get`/`set` call.
			// Injected .NET indexer `c[i]` / `c[i] = v` -> the DEFAULT INDEXED PROPERTY of the constructed .NET type.
			// kotc emits the FAITHFUL Kotlin get/set operator identity (`method:"get"/"set"`) plus an index marker
			// (`"prop":"index-get"/"index-set"`, extending step 3's accessor-KIND mechanism); it does NOT bake the CLR
			// slot name. bir2cir's NetInteropBinding reflects the .NET type's default indexed property off the refs (its
			// DefaultMember / `[IndexerName]` name) -> its `get_`/`set_` accessor method, emitting the plain `clrInstance`
			// call — byte-identical to the old hardcoded `get_Item`/`set_Item` for the standard case, but correct for a
			// custom-named indexer. The receiver's type carries the element type arg (`Collection<Int>`), so the
			// constructed `clrg:...[int]` resolves the substituted accessor.
			val ixOwner = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
			if (recv != null && ixOwner != null && clrName(ixOwner) != null) {
				val mt = birType(recv.type); val a = regularArgs(call)
				// The get accessor returning a generic param (`IList<T>.get` -> T) reports the SUBSTITUTED ret (gp:T):
				// ilemit then hands back gp:T (matching the stack), so the value<->collection boundary box/unbox is
				// correctly typed (else a value-type instantiation NullRefs/garbages). Needs ClrRef("gp:") -> MapType.
				val retH = birType(call.type)
				return if (name == "get")
					"""{"k":"callInstance","ownerType":${str(mt)},"method":"get","prop":"index-get","argTypes":[${birType(a[0].type).toJson()}],"ret":${str(retH)},"recv":${expr(recv)},"args":[${expr(a[0])}]}"""
				else
					"""{"k":"callInstance","ownerType":${str(mt)},"method":"set","prop":"index-set","argTypes":[${birType(a[0].type).toJson()},${birType(a[1].type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"recv":${expr(recv)},"args":[${expr(a[0])},${expr(a[1])}]}"""
			}
		}

		// BCL interop: a call whose declaring class is a .NET type (`@Clr` or injected) resolves to a real .NET
		// member. An INHERITED .NET member (e.g. `appError.Message`) is a fake-override whose `parent` is the
		// Kotlin subclass, so resolve through the fake override to find the real .NET declaring type.
		// clrName resolves the .NET TYPE name only: a `kotlin.*` stdlib owner resolves to null here, so its member call
		// FALLS THROUGH to the plain Kotlin member-call path below (bir2cir substitutes it from the ref.dll). Only a
		// genuine .NET interop owner (facadegen-injected, resolved off its IR ClassId) keeps a non-null clrType.
		val clrTypeName = declaringClass?.let { clrName(it) }
			?: (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass)?.let { clrName(it) }
			// A synthesized companion of an injected .NET type holds its STATIC members (`App.Start`) -> a static call
			// on the .NET type itself.
			?: declaringClass?.takeIf { it.isCompanion }?.let { it.parent as? IrClass }?.let { clrName(it) }
		val clrType = clrTypeName?.let { TypeNode.Fqn(it) }
		if (clrType != null) {
			val recv = dispatchReceiver(call)
			val isStatic = recv == null || recv is IrGetObjectValue
			// Address the member on the CONSTRUCTED .NET type (`clrg:Collection[int]`) so a member of a generic
			// instantiation resolves. Two cases: (1) the receiver's own type IS the .NET type; (2) the member is
			// INHERITED from a .NET base (receiver is a Kotlin subclass) -> use the subclass's .NET supertype,
			// which carries the concrete type args (`class C : Collection<Int>`).
			val recvClass = recv?.type?.classifierOrNull?.owner as? IrClass
			// The REAL .NET declaring type (resolve the fake override; `declaringClass` would be the subclass).
			val declClass = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
			val memberType = when {
				isStatic || recv == null -> clrType
				recvClass != null && clrName(recvClass) != null -> birType(recv.type)
				// A type-PARAM receiver (`destination: C` where `C : MutableCollection<T>`, e.g. filterTo's body) has no
				// recvClass -> use the type param's @Clr-bound BOUND with its args (clrg:ICollection[T]), not the raw
				// clrName (System.Collections.Generic.ICollection without `1 -> ResolveType fails).
				else -> (recvClass?.superTypes ?: (recv?.type?.classifierOrNull?.owner as? org.jetbrains.kotlin.ir.declarations.IrTypeParameter)?.superTypes)
					?.firstOrNull { it.classifierOrNull?.owner == declClass }?.let { birType(it) } ?: clrType
			}
			// A .NET event is NOT rewritten to an `add_<E>`/`remove_<E>` call (clrEventAdd) here. It is
			// surfaced as a `kotlin.clr.ClrEvent<T>` property and subscribed via `w.<E> += handler` / `-= handler`; kotc
			// emits the plain `plusAssign`/`minusAssign` operator call (handled at the top of this function), and bir2cir's
			// ClrEventOperatorBinding binds it to the .NET add/remove accessor. No `add_`/`remove_` naming, no clrEventAdd
			// in kotc — the Kotlin<->CLR event relation is bir2cir's (layer purity).
			// A generic .NET method (`Unsafe.SizeOf<T>()`, `Activator.CreateInstance<T>()`) -> resolve the open
			// generic-method definition by name + type-arity + parameter shapes, then MakeGenericMethod with the
			// call's type args. The CLR has reified generics, so this is just an ordinary generic-method call (no
			// erasure dance) — see [[clr-not-jvm-discard-jvmisms]]. Static -> clrGenericStatic, instance -> ...Instance.
			if (callee.typeParameters.isNotEmpty()) {
				val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
				if (targs.all { it != null }) {
					val taJson = targs.joinToString(",") { birType(it!!).toJson() }
					val member = objectMethodName(callee) ?: name
					// A generic MEMBER extension (`class C { fun <R> T.f() }`): the `__self` receiver is the .NET method's
					// first param -> prepend its value + shape so by-shape overload resolution and the call line up.
					val gExt = if (!isStatic) extensionReceiver(call) else null
					val shapeParams = (if (gExt != null) listOf(gExt.type) else emptyList()) + regularParams(callee).map { it.type }
					// kotc emits the DECLARED parameter types as PURE-KOTLIN `birType` identities (`shapeTypes`); bir2cir
					// DERIVES the ilemit `shapes` overload-matcher tokens (the .NET simple names Int64/SByte/… + gp/
					// generic/ienum/func:N) off the @ClrTypeAlias index and drops `shapeTypes`. No CLR-shape knowledge here.
					val shapeTypes = shapeParams.joinToString(",") { birType(it).toJson() }
					val argsJson = (listOfNotNull(gExt) + regularArgs(call)).joinToString(",") { expr(it) }
					// A `suspend` generic .NET-member callee carries the `"suspendCall":true` FACT for bir2cir's deferred
					// Task/await lowering, exactly like the non-generic call paths (suspendCallTag) — otherwise a generic
					// .NET-member suspend call would silently drop out of the suspension lowering. (latent ⑤.)
					// A2 (#61): a PLAIN call by identity carrying the generic FACTS (typeArgs + declared shapeTypes);
					// bir2cir's NetInteropBinding resolves the owner off the .NET refs and shapes it to clrGenericStatic/
					// clrGenericInstance (the `typeArgs` presence is the generic signal).
					return if (isStatic)
						"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[$argsJson]${suspendCallTag(callee)}}"""
					else
						"""{"k":"callInstance","ownerType":${memberType!!.toJson()},"method":${str(member)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"recv":${expr(recv!!)},"args":[$argsJson]${suspendCallTag(callee)}}"""
				}
			}
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) {
				// A `kotlin.clr.ClrEvent<T>` property read is legal ONLY as the receiver of a `+=`/`-=` subscription
				// (`w.Changed += h`), where clrEventReceiverOk is set. A bare read (`val e = w.Changed`) would emit a
				// `clrPropGet get_<Event>` that no bir2cir rule strips -> a distant, diagnostic-free downstream failure.
				// A .NET event is not a first-class value, so reject it here at the source with a kotc compile error.
				if (!clrEventReceiverOk && callee === prop.getter
					&& callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
					hadError = true
					messageCollector?.report(CompilerMessageSeverity.ERROR,
						"a .NET event ('${prop.name.asString()}') is not a first-class value: it may only appear as the " +
							"left-hand side of a '+=' / '-=' subscription (e.g. `x.${prop.name.asString()} += handler`), not be read/assigned",
						locationOf(call))
					return """{"k":"unsupportedExpr","of":"clr-event-read-outside-subscription: ${prop.name.asString()}"}"""
				}
				// A2 step 3: the property's OWN Kotlin name IS the .NET slot identity (facadegen injects the member under
				// its .NET name), so kotc reads NO CLR name here — it emits the bare property name + the accessor KIND
				// (`"prop":"get"/"set"`, a frontend fact from correspondingPropertySymbol). bir2cir's NetInteropBinding
				// applies the .NET `get_`/`set_` accessor convention off the refs.
				val pn = prop.name.asString()
				val recvJson = if (isStatic) "null" else expr(recv!!)
				// A restored MEMBER extension property (`class C { val T.p }`): no .NET property exists — it's a
				// `get_p(__self)`/`set_p(__self, v)` method on the dispatch type, the extension receiver as `__self`.
				// A2 (#61): a PLAIN instance call by identity carrying the accessor KIND; bir2cir's NetInteropBinding
				// finds NO .NET property `p` (it's a synthetic accessor method) and applies the convention -> a clrInstance
				// get_p/set_p method call.
				extensionReceiver(call)?.let { pExt ->
					return if (callee === prop.setter)
						"""{"k":"callInstance","ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set","argTypes":[${birType(pExt.type).toJson()},${birType(regularArgs(call).first().type).toJson()}],"ret":${fqnJson("kotlin.Unit")},"recv":$recvJson,"args":[${expr(pExt)},${expr(regularArgs(call).first())}]}"""
					else """{"k":"callInstance","ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get","argTypes":[${birType(pExt.type).toJson()}],"ret":${birType(callee.returnType).toJson()},"recv":$recvJson,"args":[${expr(pExt)}]}"""
				}
				// A2 (#61): a `kotlin.clr.ClrEvent<T>` read is CLR-ONLY vocabulary — a .NET event has no plain-Kotlin
				// call form (it exposes add_/remove_, not a get_); facadegen injects it purely to typecheck, so kotc
				// LOWERS it directly to a DEDICATED dialect node `clrEventGet` (the ClrEvent<T> handle) — NOT the
				// bir2cir-produced `clrPropGet` (which after A2 means a real .NET property). It exists ONLY to feed a
				// `+=`/`-=`: bir2cir's ClrEventOperatorBinding consumes the `clrEventGet + plusAssign/minusAssign` pair
				// into an add_/remove_ accessor, so it never reaches ilemit (a bare event read is rejected above). Every
				// OTHER property is a plain Kotlin-shaped access -> emit the get_/set_ accessor CALL by identity;
				// NetInteropBinding shapes it to clrPropGet/clrPropSet (a .NET property OR field) off the refs.
				if (callee.returnType.classFqName?.asString() == "kotlin.clr.ClrEvent") {
					return """{"k":"clrEventGet","type":${memberType!!.toJson()},"name":${str(pn)},"static":$isStatic,"recv":$recvJson}"""
				}
				val propCallKind = if (isStatic) "callStatic" else "callInstance"
				val propRecvField = if (isStatic) "" else ""","recv":$recvJson"""
				return if (callee === prop.setter)
					"""{"k":"$propCallKind","ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"set","argTypes":[${birType(regularArgs(call).first().type).toJson()}]$propRecvField,"args":[${expr(regularArgs(call).first())}]}"""
				else """{"k":"$propCallKind","ownerType":${memberType!!.toJson()},"method":${str(pn)},"prop":"get","argTypes":[],"ret":${birType(callee.returnType).toJson()}$propRecvField,"args":[]}"""
			}
			val member = objectMethodName(callee) ?: name
			val argsJson = regularArgs(call).joinToString(",") { expr(it) }
			// kotc emits the PLAIN Kotlin return type; a `suspend` callee is marked by `suspendTag` only (the Task/await
			// lowering is a deferred downstream layer). No coroutine ABI (Task<T>) is baked here.
			val ret = birType(callee.returnType).toJson()
			val suspendTag = suspendCallTag(callee)
			// A .NET operator (`Vec2 + Vec2` -> op_Addition) is emitted here as the PLAIN Kotlin operator identity
			// (`callInstance method="plus" recv:<a> args:[<b>]`); bir2cir's NetInteropBinding resolves the owner off
			// the .NET refs, confirms the CLR type declares the `op_X` static, and reshapes it to a `clrStatic op_X`
			// with the receiver prepended. No `op_` naming / receiver-prepend here (layer purity — CLR knowledge is bir2cir's).
			// A .NET extension method `static M(this T self, …)` exposed as a Kotlin extension `fun T.m()` on a @Clr
			// object: it's a STATIC call whose first argument is the extension receiver.
			val extRecv = extensionReceiver(call)
			if (isStatic && extRecv != null) {
				val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
				val allArgTypes = (listOf(birType(extRecv.type).toJson()) + regularArgs(call).map { birType(it.type).toJson() }).joinToString(",")
				return """{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"args":[$allArgs]$suspendTag}"""
			}
			// A restored MEMBER extension function (`class C { fun T.f() }`): an INSTANCE method on the dispatch receiver
			// (C) whose first .NET param `__self` is the extension receiver -> dispatch on `recv`, prepend the receiver.
			if (!isStatic && extRecv != null && recv != null) {
				val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
				val allArgTypes = (listOf(birType(extRecv.type).toJson()) + regularArgs(call).map { birType(it.type).toJson() }).joinToString(",")
				return """{"k":"callInstance","ownerType":${memberType!!.toJson()},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"recv":${expr(recv)},"args":[$allArgs]$suspendTag}"""
			}
			// A2 (#61): a PLAIN static/instance call by the .NET owner's FQN identity; bir2cir's NetInteropBinding
			// resolves the owner off the .NET refs and shapes it (clrStatic/clrInstance). No .NET-shape decision here.
			val (cArgs, cArgTypes) = clrCallArgs(call, callee)
			return if (isStatic)
				"""{"k":"callStatic","ownerType":${clrType!!.toJson()},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"args":[$cArgs]$suspendTag}"""
			else
				"""{"k":"callInstance","ownerType":${memberType!!.toJson()},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"recv":${expr(recv!!)},"args":[$cArgs]$suspendTag}"""
		}

		// Companion-object member -> a static member of the enclosing class (precedes user-property field access).
		// A super-typed companion is a real singleton (<Outer>.InstanceClass) instead: its members are NOT static on the
		// parent, so fall through to the normal instance-call path (receiver = the companion-as-value -> INSTANCE).
		(callee.parent as? IrClass)?.takeIf { it.isCompanion && superTypedCompanion(it.parent as IrClass) == null }?.let { comp ->
			val enclosing = typeName(comp.parent as IrClass)
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) {
				// A companion EXTENSION property (`val Int.seconds` on Duration.Companion) is NEVER a static field —
				// extension properties have no backing field (a cross-module deserialized stub may claim one; trusting
				// it dropped the receiver entirely: `2.seconds` emitted a bare `staticField Duration.seconds`, and the
				// in-module getter path emitted `get_milliseconds` with `"args":[]`). Mirror the top-level-property
				// branch: the static get_/set_<name>(__self, ...) on the enclosing class with the receiver as the
				// leading arg; `sig` picks the right overload (get_seconds(Int|Long|Double)).
				val ext = extensionReceiver(call)
				if (ext != null) return if (callee === prop.setter) {
					val args = listOf(ext) + regularArgs(call)
					"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str("set_" + prop.name.asString())}${overloadSigField(callee)},"args":[${args.joinToString(",") { expr(it) }}]}"""
				} else
					"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str("get_" + prop.name.asString())}${overloadSigField(callee)},"args":[${expr(ext)}]${retHint(false, call.type)}}"""
				// A companion COMPUTED property (`val X.Companion.foo: T get() = ...`, no backing field) -> a static
				// call by the property's OWN bare Kotlin identity + a `"prop":"get"/"set"` marker (#78; the SAME
				// A2 convention already used for the restored top-level property, `pn`/`"prop"` above) — NOT the
				// baked `get_`/`set_` slot name. kotc does not know whether the enclosing class is CLR-bound (a
				// stdlib @ClrTypeAlias owner) or plain Kotlin; bir2cir's MemberCallSubstitution reads the stdlib
				// @ClrProperty/@ClrIntrinsic metadata off the ref.dll by this bare name and shapes the .NET
				// accessor, falling back to kotc's own get_/set_<name> declaration convention when no binding exists.
				return if (callee === prop.setter)
					if (prop.backingField == null)
						"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"set","args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
					else
						"""{"k":"staticFieldSet","ownerType":${fqnJson(enclosing)},"name":${str(prop.name.asString())},"value":${expr(regularArgs(call).first())}}"""
				else if (prop.backingField == null)
					"""{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(prop.name.asString())},"prop":"get","args":[]${retHint(false, call.type)}}"""
				else """{"k":"staticField","ownerType":${fqnJson(enclosing)},"name":${str(prop.name.asString())}}"""
			}
			// A generic companion fun (`Result.Companion.success<T>`) carries its resolved type args — without them
			// the emitted call references the uninstantiated generic method (invalid IL on a generic enclosing class).
			return """{"k":"callStatic","owner":${fqnJson(enclosing)},"method":${str(name)}${overloadSigField(callee)}${typeArgsJson(call)},"args":[${filledArgs(call).joinToString(",")}]}"""
		}

			// An INJECTED top-level property (from a DotKt assembly) -> the referenced .NET file class holds it. An
			// EXTENSION property (`val T.p`) surfaces as get_/set_<name>(__self) statics with the extension receiver
			// passed as `__self`; a plain NON-extension property (`val greeting`) is a plain STATIC FIELD (no accessor
			// exists), so read -> `staticField` / write -> `staticFieldSet` of that referenced file class (#34b).
			// (body==null = injected stub.)
			(callee.correspondingPropertySymbol?.owner)?.let { p ->
				// A2 stage 3: read the restored top-level property's .NET file-facade class off its RESOLVED IR
				// `CallableId` (`package` + name).
				if (declaringClass == null) (p.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
					?.let { kotc.frontend.clrInjectedTopLevelPropFileClass(CallableId(it.packageFqName, p.name)) }?.let { fileClass ->
					val isExt = p.getter?.parameters?.any { it.kind == IrParameterKind.ExtensionReceiver } == true
					if (!isExt) {
						// A plain top-level val/var restored from a referenced DotKt library -> a STATIC FIELD of its .NET
						// file class. NOT get_/set_ (a plain val/var has no accessor) and NOT `fileClassOf(p)` (the parent is
						// an IrPackageFragment, so that returns the CURRENT file — the wrong owner). Use the referenced class.
						return if (callee === p.setter)
							"""{"k":"staticFieldSet","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
						else """{"k":"staticField","ownerType":${fqnJson(fileClass)},"name":${str(p.name.asString())}}"""
					}
					val recv = extensionReceiver(call)
					// A2 (#61 / step 3): a top-level EXTENSION property accessor is a static `get_/set_<name>(__self)` METHOD
					// on the referenced file class (NOT a .NET property) -> emit the plain static call by identity carrying
					// the accessor KIND; bir2cir's NetInteropBinding finds no matching .NET property/field and applies the
					// `get_`/`set_` convention -> a clrStatic method call.
					if (callee === p.setter) {
						val args = listOfNotNull(recv) + regularArgs(call)
						return """{"k":"callStatic","ownerType":${str(fileClass)},"method":${str(p.name.asString())},"prop":"set","argTypes":[${args.joinToString(",") { birType(it.type).toJson() }}],"ret":${fqnJson("kotlin.Unit")},"args":[${args.joinToString(",") { expr(it) }}]}"""
					}
					return """{"k":"callStatic","ownerType":${str(fileClass)},"method":${str(p.name.asString())},"prop":"get","argTypes":[${recv?.let { birType(it.type).toJson() } ?: ""}],"ret":${birType(callee.returnType).toJson()},"args":[${recv?.let { expr(it) } ?: ""}]}"""
				}
			}

		// Top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING file's
		// class. Use the property's own file, NOT the file currently being emitted — else a cross-file reference
		// looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			if (declaringClass == null) {
				val ext = extensionReceiver(call)
				// C7: a TOP-LEVEL EXTENSION property (`val List<T>.lastIndex`, `val Int.absoluteValue`, `val
				// CharSequence.indices`) has NO real static field — its value is a get_/set_<name>(__self) static whose
				// leading arg is the extension receiver. Route it EXACTLY like a top-level extension FUNCTION: owner=null,
				// so bir2cir attributes it to the ref.dll file class in a cross-module app build (and a same-module sibling
				// stays owner-less for ilemit's FindStatic). `sig` disambiguates a same-name overload by receiver type.
				// A cross-module DESERIALIZED stub can spuriously report a backing field, so an extension property must
				// NEVER fall to the static-field read below — that dropped the receiver and looked up `<CurrentFileKt>.
				// <name>` as a field (the C7 `field AppKt.lastIndex not found` crash).
				if (ext != null) {
					// A GENERIC extension property (`val List<T>.lastIndex`/`.indices`) has a generic get_<name>[T] static —
					// carry the resolved type args (+ a retType hint) so ilemit MakeGenericMethods it; without them the call
					// hits the uninstantiated generic method ("type is not fully instantiated"). Mirrors the generic
					// extension-FUNCTION path. A non-generic getter (Int.absoluteValue, CharSequence.lastIndex) emits no ta.
					val ta = typeArgsJson(call)
					return if (callee === p.setter) {
						val args = listOf(ext) + regularArgs(call)
						"""{"k":"callStatic","owner":null,"method":${str("set_" + p.name.asString())}${overloadSigField(callee)}$ta,"args":[${args.joinToString(",") { expr(it) }}]}"""
					} else
						"""{"k":"callStatic","owner":null,"method":${str("get_" + p.name.asString())}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), birType(call.type))},"args":[${expr(ext)}]}"""
				}
				// A plain top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING
				// file's class. Use the property's own file, NOT the file currently being emitted — else a cross-file
				// reference looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
				val owner = fileClassOf(p)
				if (p.backingField == null)
					return if (callee === p.setter)
						"""{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("set_" + p.name.asString())},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
					else
						"""{"k":"callStatic","owner":${fqnJson(owner)},"method":${str("get_" + p.name.asString())},"args":[]${retHint(false, call.type)}}"""
				return if (callee === p.setter)
					"""{"k":"staticFieldSet","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"staticField","ownerType":${fqnJson(owner)},"name":${str(p.name.asString())}}"""
			}
		}

		// `s.length` on a String is NOT intercepted here: it's a real `kotlin.String.length` property read — fall
		// through to the ordinary property-get path so it emits as a `kotlin.String` `get_length` member call. The
		// CLR binding (String.length -> System.String.Length) is stdlib `@ClrIntrinsic("Length")` metadata, applied
		// by bir2cir's MemberCallSubstitution (the sibling `String.get`->`get_Chars` was cleaned the same way). kotc
		// carries NO CLR knowledge here (layer boundary — CLAUDE.md §"kotc reads NEITHER @ClrIntrinsic…").
		// Pair/Triple `.first`/`.second`/`.third` -> stdlib class fields.
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			val pfq = (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			if (pfq == "kotlin.Pair" || pfq == "kotlin.Triple") {
				val field = p.name.asString().takeIf { it in setOf("first", "second", "third") }
				if (field != null) dispatchReceiver(call)?.let { r ->
					return """{"k":"field","ownerType":${birType(r.type).toJson()},"recv":${expr(r)},"name":${str(field)}}"""
				}
			}
			// `IndexedValue.index`/`.value` -> stdlib class fields.
			if (pfq == "kotlin.collections.IndexedValue") {
				val field = p.name.asString().takeIf { it in setOf("index", "value") }
				if (field != null) dispatchReceiver(call)?.let { r ->
					return """{"k":"field","ownerType":${birType(r.type).toJson()},"recv":${expr(r)},"name":${str(field)}}"""
				}
			}
		}

		// Property get/set on a user class -> field access.
		val property = callee.correspondingPropertySymbol?.owner
		// `.size` -> CIL array length (arrays) or `Enumerable.Count` (collections).
		if (property?.name?.asString() == "size") dispatchReceiver(call)?.let { r ->
			if (isArrayType(r.type)) return """{"k":"arrayLen","array":${expr(r)}}"""
			// `Color.entries.size`: entries -> a Color[] (enumValues), so .size is the array length.
			if (r.type.classFqName?.asString() == "kotlin.enums.EnumEntries") return """{"k":"arrayLen","array":${expr(r)}}"""
			// kotlin.* collection/map `.size` is NOT intercepted: it's a real `size` property — fall through to the
			// ordinary property read so it emits as a kotlin.* `get_size` call.
		}
		// `kProperty.name` is NOT intercepted here (#70): `kotlin.reflect.KProperty*`/`KCallable.name` is a REAL
		// emitted stdlib interface member now (kotc's `propertyRef`/`kPropertyStub` materialize real implementations
		// of it) — it falls through to the ordinary member-property-read path below, emitting the SAME
		// `callInstance ownerType:kotlin.reflect.KProperty(/KCallable) method:get_name` shape this used to hand-roll,
		// just with the real FQN instead of the retired `dotkt$KProperty` synthetic.
		// Delegated property access. `by lazy`: `obj.x` -> `obj.x$delegate.value` (a plain `kotlin.Lazy<T>::get_value`
		// read; see the lazy case below), dropping thisRef/KProperty. Custom (duck-typed) delegate: route to its
		// getValue/setValue, passing thisRef and a materialized `KProperty` (compiler-generated). Stdlib-interface
		// delegates -> deferred.
		if (property != null && property.isDelegated && declaringClass != null) {
			val bf = property.backingField
			val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
			val delegate = bf?.let { """{"k":"field","ownerType":${fqnJson(typeName(declaringClass))},"recv":$recv,"name":${str(it.name.asString())}}""" }
			// `by lazy` (member): the delegate is a real `kotlin.Lazy<T>` (the stdlib `UnsafeLazyImpl`). Its accessor is
			// the InlineOnly `Lazy<T>.getValue(…) = value` operator, whose stdlib inline body is absent from our IR;
			// inline it (a pure Kotlin-frontend fact) to a plain read of the Lazy interface's `value` getter. bir2cir/
			// ilemit resolve the real emitted `kotlin.Lazy::get_value` — no CLR (System.Lazy) knowledge in kotc.
			if (callee === property.getter && bf?.type?.classFqName?.asString() == "kotlin.Lazy") {
				val owner = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
				return """{"k":"callInstance","ownerType":${owner.toJson()},"virtual":true,"recv":$delegate,"method":"get_value","args":[]${retHint((owner as? TypeNode.Fqn)?.args != null, callee.returnType)}}"""
			}
			// `val x by map` is NOT intercepted: FIR routes it through the stdlib `Map.getValue`/`setValue` operator —
			// fall through to the getValue/setValue delegate routing so it emits as real kotlin.* calls.
			// Route getValue/setValue to the delegate object. The dispatch type is either the concrete user
			// delegate class (duck-typed or implementing Read(Write)Property) or — when the field is typed as
			// the Read(Write)Property interface (e.g. `by Delegates.observable(…)`, `by Delegates.notNull()`) —
			// the REAL generic stdlib `kotlin.properties.Read(Write)Property<T,V>` interface. That mirrors the
			// `by lazy` path (dispatch on the real generic `kotlin.Lazy<T>`): the delegate value is the real
			// emitted stdlib `ObservableProperty`/`NotNullVar`, which implements the real generic interface, so
			// the call binds to the actual stdlib getValue/setValue — no compiler-synthesized delegate class.
			val delegateClass = bf?.type?.classifierOrNull?.owner as? IrClass
			val isUserDelegate = delegateClass != null && clrName(delegateClass) == null &&
				delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true
			val bfFq = bf?.type?.classFqName?.asString()
			val (owner, ownerGeneric) = when {
				isUserDelegate -> str(typeName(delegateClass!!)) to false
				bf != null && (bfFq == "kotlin.properties.ReadWriteProperty" || bfFq == "kotlin.properties.ReadOnlyProperty") -> {
					val os = ownerSpec(bf.type.classifierOrNull?.owner as? IrClass, bf.type)
					os.toJson() to ((os as? TypeNode.Fqn)?.args != null)
				}
				else -> null to false
			}
			if (delegate != null && owner != null) {
				val kprop = kPropertyStub(property.name.asString())
				// callvirt: getValue/setValue is virtual (interface impl) or final (duck-typed) — callvirt fits both.
				return if (callee === property.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"setValue","args":[$recv,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"getValue","args":[$recv,$kprop]${retHint(ownerGeneric, callee.returnType)}}"""
			}
			// `val x by map` (a TOP-LEVEL-extension delegate convention): FIR resolved the accessor to the stdlib
			// `kotlin.collections.getValue/setValue(thisRef, property)` extension (MapAccessors.kt) — the resolved
			// symbol sits in the accessor's own generated body. Re-emit it at the access site as the plain owner-null
			// static call the general top-level-extension path produces (receiver-first args + declared sig +
			// typeArgs), so bir2cir/ilemit resolve the real rt-stdlib method like any other cross-module stdlib call.
			// (Pure Kotlin: the target comes from FIR resolution, no CLR knowledge here.)
			run {
				val accessor = callee as? IrSimpleFunction ?: return@run
				val stmts = (accessor.body as? IrBlockBody)?.statements ?: return@run
				val bodyCall = stmts.mapNotNull { st -> (st as? IrReturn)?.value as? IrCall ?: st as? IrCall }.singleOrNull() ?: return@run
				val target = bodyCall.symbol.owner
				if (delegate == null || target.parent is IrClass) return@run
				if (target.name.asString() != "getValue" && target.name.asString() != "setValue") return@run
				val kprop = kPropertyStub(property.name.asString())
				val ta = typeArgsJson(bodyCall)
				val setArg = if (callee === property.setter) ",${expr(regularArgs(call).first())}" else ""
				return """{"k":"callStatic","owner":null,"method":${str(target.name.asString())}${overloadSigField(target)}$ta${retHintStr(ta.isNotEmpty(), birType(callee.returnType))},"args":[$delegate,$recv,$kprop$setArg]}"""
			}
			return unsupported(call, "this delegated property",
				"its delegate type could not be resolved to a supported form (lazy, a custom getValue/setValue, or a Map)")
		}
		if (property != null && declaringClass != null) {
			val recvExpr = dispatchReceiver(call)
			val recv = recvExpr?.let { expr(it) } ?: """{"k":"this"}"""
			val ownerStr = ownerSpec(declaringClass, recvExpr?.type)
			val owner = str(ownerStr)
			// A property with a custom accessor — OR one overriding a .NET/synthetic-mapped iface property (e.g.
			// CharSequence.length -> get_length) — routes through the get_/set_ method, not the backing field.
			val ifaceAcc = clrIfaceMemberName(callee)
			if (!property.isLateinit && !isClrField(property)) {   // route through get_/set_ accessor (CLR property model); @ClrField reads/writes the plain field
				val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
				// A MEMBER extension property (`class C { val T.p get() }`): dispatch on the enclosing C, but its `get_p`/
				// `set_p` method takes the extension receiver as a leading `__self` arg -> prepend it.
				val pExt = extensionReceiver(call)?.let { expr(it) }
				return if (callee === property.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(ifaceAcc ?: "set_" + property.name.asString())},"args":[${listOfNotNull(pExt, expr(regularArgs(call).first())).joinToString(",")}]${overridesJson(callee)}}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(ifaceAcc ?: "get_" + property.name.asString())},"args":[${pExt ?: ""}]${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}${overridesJson(callee)}}"""
			}
			return if (callee === property.setter)
				"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			// `lateinit var` read -> throw if still uninitialized (the field is null) — proper lateinit semantics.
			else if (property.isLateinit)
				"""{"k":"lateinitGet","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}}"""
			else """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}${retHint((ownerStr as? TypeNode.Fqn)?.args != null, call.type)}}"""
		}

		// Kotlin universal methods (hashCode/toString/equals) on a builtin receiver. The System.Object slot is correct
		// ONLY for a GENUINE universal call — one whose receiver TYPE does not declare its OWN routable override:
		//  - the resolved callee is the inherited kotlin.Any member (a fake override): Int/Long/Char/Boolean.hashCode,
		//    or a bare List/Set/Map.toString (emitted as objMethod ToString with a `recvType` hint; bir2cir routes it
		//    Kotlin-style), or Any/generic; and
		//  - a PRIMITIVE value type's toString/equals — those are declared but bodyless (no Kotlin body to hoist, no
		//    @ClrIntrinsic), so bir2cir has nothing to route to and the BCL value type's ToString/Equals IS correct.
		// When the receiver TYPE declares its OWN routable override — String's polynomial hashCode / Double|Float's
		// deterministic bit-hash (a real Kotlin body → C5), a Pair|Triple|data-class toString and String's
		// @ClrIntrinsic toString/equals (→ C11) — the call must REACH that member, so FALL THROUGH to the ordinary
		// member-call path (bir2cir routes it: a real body → rule-3 helper, an @ClrIntrinsic → its BCL slot). Routing a
		// declared override to System.Object here shadows the correct Kotlin body — the C5/C11 miscompiles.
		if (isBuiltin && dispatchReceiver(call) != null) {
			// The receiver TYPE declares its OWN override iff the resolved callee is a real (non-fake-override) member of a
			// type OTHER than kotlin.Any. A call resolved DIRECTLY to `kotlin.Any.hashCode/toString/equals` — e.g.
			// `element.toString()` on a generic `T` with no more-derived override — is NOT a fake override yet IS the
			// universal method, so it must keep the System.Object slot (falling through would emit a call to the
			// non-existent `kotlin.Any.toString` and NRE). Hence the explicit kotlin.Any exclusion beside isFakeOverride.
			val declaresOwn = !callee.isFakeOverride && declaringClass?.fqNameWhenAvailable?.asString() != "kotlin.Any"
			val primitive = dispatchReceiver(call)!!.type.isPrimitiveOrUnsigned()
			val fallThrough = when (name) {
				"hashCode" -> declaresOwn                      // Int/Long/Char/Boolean inherit Any.hashCode → stays objMethod
				"toString", "equals" -> declaresOwn && !primitive
				else -> false
			}
			if (!fallThrough) when (name) {
				"hashCode" -> return """{"k":"objMethod","method":"GetHashCode","recv":${expr(dispatchReceiver(call)!!)}}"""
				"toString" -> if (regularArgs(call).isEmpty()) {
					// Emit the FAITHFUL objMethod ToString. bir2cir recovers the receiver's static type via StaticType (no
					// kotc hint) and, for a collection/Map receiver, routes to the Kotlin-style clrCollToString /
					// clrMapToString helper (`[a, b]` / `{a=1, b=2}`); else it keeps the raw .NET ToString.
					val recvE = dispatchReceiver(call)!!
					return """{"k":"objMethod","method":"ToString","recv":${expr(recvE)}}"""
				}
				"equals" -> {
					val recvE = dispatchReceiver(call)!!; val argE = regularArgs(call).first()
					// Emit the FAITHFUL objMethod Equals. An EXPLICIT `.equals()` on a boxed Double/Float / a collection
					// follows Kotlin's TOTAL order / STRUCTURAL equality (Object.Equals gives IEEE
					// `(-0.0).equals(0.0)==true` / reference identity), so bir2cir recovers the receiver/arg static types
					// via StaticType (no kotc hint) and routes to the SAME helper the EQEQ path uses; else it keeps
					// Object.Equals.
					return """{"k":"objMethod","method":"Equals","recv":${expr(recvE)},"arg":${expr(argE)}}"""
				}
			}
		}
		// `n.toString(radix)` is NOT lowered in kotc (C4, 2026-07-06). The former `System.Convert.ToString(value, base)`
		// special-case was BOTH a layer violation (a BCL name in kotc) AND wrong: Convert.ToString renders a negative in
		// two's-complement (`(-255).toString(16)` -> "ffffff01", not "-ff") and THROWS for a base outside {2,8,10,16}
		// (`35.toString(36)` -> ArgumentException "Invalid Base"). The stdlib actual (StringNumberConversionsClr.kt) has
		// the correct sign-and-arbitrary-digit body; kotc now emits the plain `kotlin.text` Int/Long.toString(radix)
		// extension call and bir2cir attributes it to StringNumberConversionsKt so the real body runs.

		if (isBuiltin) {
			val operands = call.arguments.filterNotNull()
			// `String + x` (concatenation, not numeric add) is NOT recognized here: kotc emits the plain
			// `callInstance kotlin.String.plus` (a faithful member call) via the general member-call path, and bir2cir's
			// PrimitiveOperatorLowering re-emits the `concat` (recovering each part's static type via StaticType, applying
			// the collection/nullable part routing) — the `String.plus -> concat` MEMBER recognition is bir2cir's.
			// `==` (EQEQ) / `===` (EQEQEQ) are `kotlin.internal.ir` COMPILER INTRINSICS. ALL of the ceq-vs-Object.Equals
			// SPLIT + the Kotlin-SEMANTIC structural routings (collection `==`, boxed Double/Float total-order `==`)
			// recognition lives in bir2cir: kotc emits ONLY the FAITHFUL intrinsic call with owner =
			// `kotlin.internal.ir` (collision-safe). PrimitiveOperatorLowering recovers the operands' SURFACE static type
			// (prim fast-path -> ceq) and VALUE static type (collection/float helpers, else objEq) via StaticType; no
			// argTypes/argValueTypes hints are emitted — the operand expression nodes + the local env carry the types.
			if (name == "EQEQ" && operands.size == 2)
				return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
			if (name == "EQEQEQ" && operands.size == 2)
				return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"EQEQEQ","args":[${expr(operands[0])},${expr(operands[1])}]}"""
			// The IR comparison intrinsics (`kotlin.internal.ir.less`/`lessOrEqual`/`greater`/`greaterOrEqual` — the
			// `<`/`<=`/`>`/`>=` desugarings, top-level with plain value params). Recognition + operand shaping is
			// bir2cir's: kotc emits ONLY the FAITHFUL intrinsic call with owner = its home package `kotlin.internal.ir`
			// (collision-safe — a user top-level `less` is NOT `isBuiltin` and never has this owner), args = the plain
			// operand expressions. bir2cir's PrimitiveOperatorLowering re-emits `{k:binOp, op:<}` and does the operand
			// shaping (primitive gating, nullable-primitive `Nullable<T>.Value` unwrap, boxed-Any -> concrete cast) via
			// StaticType — exactly like EQEQ/EQEQEQ above. The Kotlin<->CLR relation lives there, not in kotc.
			if (name in setOf("less", "lessOrEqual", "greater", "greaterOrEqual") && operands.size == 2
					&& callee.parameters.none { it.kind == IrParameterKind.ExtensionReceiver })
				return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":${str(name)},"args":[${expr(operands[0])},${expr(operands[1])}]}"""
			// UNARY (unaryMinus/unaryPlus/not/inv) recognition is bir2cir's: kotc emits the faithful
			// `callInstance kotlin.Int.unaryMinus()` (0-arg member) and bir2cir re-emits `{k:unaryOp}` from the
			// PRIMITIVE_OP_FQ owner. The receiver is value-shaped by the general callInstance path (recvExpr).
			// `i.inc()`/`i.dec()` (the `i++`/`i--` desugaring) recognition is bir2cir's: kotc emits
			// the faithful `callInstance kotlin.Int.inc()` (0-arg member, receiver value-shaped by recvExpr) and
			// PrimitiveOperatorLowering re-emits `(recv + 1)`/`(recv - 1)` (the `const 1:kotlin.Int` literal moves there).
			// Numeric conversion `x.toLong()`/`x.toInt()`/… is not recognized here: kotc emits the plain
			// `callInstance kotlin.Int.toLong` (the faithful IR); bir2cir reads the `@kotlin.clr.ClrConv` marker off the
			// stdlib primitive's conversion member on the ref.dll and emits the `conv` node from the callee's return type.
			val fq = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
			// `println(...)`/`print(...)` are NOT recognized here: kotc emits the plain top-level `callStatic owner:null`
			// via the general call path, and bir2cir substitutes it to System.Console.Write/WriteLine off the stdlib
			// @ClrIntrinsic (runtime/stdlib/clr/kotlin/io/ConsoleClr.kt) and wraps a collection/Map arg in
			// clrCollToString/clrMapToString (Kotlin-style `[a, b]`) — recovering the operand static types via StaticType.
			// `readLine()` is NOT lowered: the CLR stdlib exposes readln()/readlnOrNull() (readlnOrNull is @ClrIntrinsic-bound
			// to System.Console.ReadLine in ConsoleClr.kt). There is no `kotlin.io.readLine` symbol in the frontend jar.
			// Regex is NOT lowered here: `kotlin.text.Regex` is
			// @ClrTypeAlias("System.Text.RegularExpressions.Regex") with `containsMatchIn`@ClrIntrinsic("IsMatch") /
			// `replace`@ClrIntrinsic("Replace") + real Kotlin bodies for `matches`/`find`/`split`/`.value`
			// (runtime/stdlib/clr/kotlin/text/regex/RegexClr.kt). kotc emits `"p".toRegex()` as a plain call to the stdlib
			// `String.toRegex()` extension (= `Regex(this)`) and `r.containsMatchIn(s)`/`r.replace(...)` as plain member
			// calls on kotlin.text.Regex; bir2cir substitutes the @ClrTypeAlias ctor + @ClrIntrinsic members off the
			// ref.dll and runs the real bodies. The Kotlin<->CLR relation lives in bir2cir, not kotc.
			// `String.format` is NOT lowered here. System.String.Format would be CLR knowledge in kotc, and it is
			// dead against the CLR frontend jar anyway — that jar has no `kotlin.text.String.Companion.format`, so the
			// symbol is unresolved before the backend ever runs. Making `String.format` work is a stdlib concern (bind a
			// `String.Companion.format(String, vararg Any?)` @ClrIntrinsic("System.String.Format")), NOT a kotc lowering.
			// Exhaustive-when synthetic else / uninitialized property -> throw (the branch is unreachable).
			// kotc names ONLY the KOTLIN exception FQN (a pure Kotlin fact); bir2cir substitutes it to the BCL type via
			// the ref.dll @ClrTypeAlias (IllegalArgumentException -> System.ArgumentException, IllegalStateException ->
			// System.InvalidOperationException). exhaustive-when synthetic-else / uninitialized-property -> IllegalState.
			if (name == "noWhenBranchMatchedException" || name == "throwUninitializedPropertyAccessException")
				return throwExpr(newExc("kotlin.IllegalStateException", str(name)))
			// Precondition / error helpers (top-level kotlin.* functions). TODO() throws NotImplementedError (a real
			// emitted Kotlin exception, NOT CLR-aliased — see Standard.kt), constructed with its standard default
			// message (the 1-arg ctor, so no cross-module default-value gap); error()/check() throw IllegalStateException.
			if (calleeFq == "kotlin.TODO") return throwExpr(newExc("kotlin.NotImplementedError", str("An operation is not implemented.")))
			if (calleeFq == "kotlin.error")
				return throwExpr("""{"k":"new","type":${fqnJson("kotlin.IllegalStateException")},"argTypes":[${fqnJson("kotlin.String")}],"args":[${regularArgs(call).firstOrNull()?.let { expr(it) } ?: """{"k":"const","type":${fqnJson("kotlin.String")},"value":"error"}"""}]}""")
			if (calleeFq == "kotlin.require")
				return """{"k":"cond","cond":${expr(regularArgs(call).first())},"then":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null},"else":${throwExpr(newExc("kotlin.IllegalArgumentException", "\"Failed requirement\""))}}"""
			if (calleeFq == "kotlin.check")
				return """{"k":"cond","cond":${expr(regularArgs(call).first())},"then":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null},"else":${throwExpr(newExc("kotlin.IllegalStateException", "\"Check failed\""))}}"""
			// `ieee754equals` is a `kotlin.internal.ir` COMPILER INTRINSIC, a sibling of EQEQ/EQEQEQ/less/... — kotc
			// emits ONLY the FAITHFUL intrinsic call with owner = `kotlin.internal.ir` (collision-safe); bir2cir's
			// PrimitiveOperatorLowering re-emits the `binOp ==` (the ordered IEEE-754 comparison). The Kotlin<->CLR
			// relation lives there, not in kotc.
			if (name == "ieee754equals" && regularArgs(call).size == 2) {
				val a = regularArgs(call)
				return """{"k":"callStatic","owner":${fqnJson("kotlin.internal.ir")},"method":"ieee754equals","args":[${expr(a[0])},${expr(a[1])}]}"""
			}
			// requireNotNull(x)/checkNotNull(x) -> evaluate once; throw if null, else the (non-null) value.
			if (calleeFq == "kotlin.requireNotNull" || calleeFq == "kotlin.checkNotNull") {
				val arg = regularArgs(call).first()
				val nv = "__rn${scopeCounter++}"
				// Kotlin: requireNotNull throws IllegalArgumentException, checkNotNull throws IllegalStateException.
				val excType = if (calleeFq == "kotlin.requireNotNull") "kotlin.IllegalArgumentException" else "kotlin.IllegalStateException"
				val velem = nullableElem(arg.type)
				val nvLoc = """{"k":"local","name":${str(nv)}}"""
				return if (velem != null) {
					// value-nullable T?: HasValue ? Value : throw.
					"""{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${TypeNode.Nullable(velem).toJson()},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${velem.toJson()},"e":$nvLoc},"then":{"k":"nullableValue","elem":${velem.toJson()},"e":$nvLoc},"else":${throwExpr(newExc(excType, "\"Required value was null\""))}}}"""
				} else {
					"""{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${birType(arg.type).toJson()},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"unaryOp","op":"!","e":{"k":"objEq","lhs":$nvLoc,"rhs":{"k":"const","type":${fqnJson("kotlin.Unit")},"value":null}}},"then":$nvLoc,"else":${throwExpr(newExc(excType, "\"Required value was null\""))}}}"""
				}
			}
			// `coerceAtMost`/`coerceAtLeast`/`coerceIn` are NOT lowered here (layer purity).
			// System.Math.Min/Max/Clamp would be a BCL name in kotc (a layer violation). The stdlib
			// `_Ranges.kt` funcs are pure Kotlin with correct bodies (`if (this < min) min else this`), so kotc now emits a
			// plain call and the real stdlib body runs. This is also MORE correct than Math.Min for floats: Kotlin's coerce
			// uses `<`/`>` (total-ordering / NaN-propagating) semantics that differ from System.Math.Min/Max on NaN.
			// (No @ClrIntrinsic needed: the pure body IS the binding — the top-preferred "emit the real body" outcome.)
			// repeat(n) { i -> body } -> an inline counter loop (no closure; body uses enclosing locals).
			if (calleeFq == "kotlin.repeat") {
				val n = regularArgs(call).getOrNull(0); val lam = regularArgs(call).getOrNull(1) as? IrFunctionExpression
				if (n != null && lam != null) {
					val vname = "__rep${scopeCounter++}"
					val itParam = lam.function.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
					itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
					val body = (lam.function.body as? IrBlockBody)?.statements.orEmpty().filter { it !is IrReturn }.joinToString(",") { stmt(it) }
					itParam?.let { valSubst.remove(it.name.asString()) }
					return """{"k":"repeatInline","var":${str(vname)},"count":${expr(n)},"body":[$body]}"""
				}
			}
			// `kotlin.math.*` is NOT lowered here. kotc emits a plain call to the stdlib fun (owner=null callStatic /
			// an extension instance for Double.pow); bir2cir's MemberCallSubstitution reads MathClr.kt's @ClrIntrinsic
			// bindings off the ref.dll and substitutes System.Math.* / System.MathF.* — the CLR relation lives there, not
			// in kotc.
			// `kotlin.text` String ops are NOT name-lowered in kotc: kotc emits a plain call; bir2cir attributes it to
			// StringsKt and the StringCharSequenceBridge (run on the RT stdlib build too) coerces the String receiver/args
			// into the `dotkt$CharSequence` adapter so the CharSequence-extension body runs (contains/indexOf/startsWith/
			// endsWith/split/substring/isEmpty/isNotEmpty/uppercase/lowercase/isBlank/etc.). Only `reversed` STAYS lowered
			// (below), pending a stdlib `StringBuilder(CharSequence)`-ctor fix.
			if (fq == "kotlin.text") {
				// `s.reversed()` -> new string(Reverse(s).ToArray()) (STAYS lowered: stdlib `StringBuilder(CharSequence)` bug).
				// M10 (#73): the real stdlib path is `CharSequence.reversed() = StringBuilder(this).reverse()`, but
				// System.Text.StringBuilder has NO ctor accepting a CharSequence (only String/Int32, Codex-confirmed), so
				// the @ClrTypeAlias ctor cannot be bound 1:1 — the compiled rt body throws InvalidProgram. Deleting this
				// branch needs a bir2cir CharSequence->String ctor-arg coercion (a separable feature), so it stays for now.
				if (name == "reversed") (extensionReceiver(call) ?: dispatchReceiver(call))?.takeIf { it.type.classFqName?.asString() == "kotlin.String" }?.let { recv ->
					return """{"k":"strReversed","s":${expr(recv)}}"""
				}
				// Every other `kotlin.text` op (trim/trimStart/trimEnd/padStart/padEnd/replace/isBlank/etc.) falls through to
				// the plain-call path above: its pure-Kotlin stdlib body runs (no BCL member name in kotc). No CLR lowering here.
			}
		}

		// DotKt round-trip: a call to a top-level function restored from a [KotlinFile] facade in a referenced
		// assembly -> a .NET static call on that file-facade class. `body == null` distinguishes the injected symbol
		// from a same-named local top-level fun. (A suspend top-level fun awaits via the coroutine path, not here.)
		if (callee.body == null && dispatchReceiver(call) == null) {
			val extRecv = extensionReceiver(call)
			// A2 stage 3: read the restored top-level function's .NET file-facade class off its RESOLVED IR `CallableId`
			// (`package` + name). FIR/Fir2Ir already resolved this call to a UNIQUE callee, so there is nothing to
			// disambiguate (a single fileClass per CallableId). `suspend` is read straight
			// off the resolved callee by `suspendCallTag(callee)` below.
			(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
				?.let { kotc.frontend.clrInjectedTopLevelFileClass(CallableId(it.packageFqName, callee.name), regularParams(callee).size) }?.let { fileClass ->
				// A cross-module `inline fun` taking a lambda (body==null here = injected stub) -> splice its carried
				// [KotlinInline] body at this call site (the only way a non-local `return` through the lambda works).
				// Splice ONLY a non-extension inline-with-lambda (the receiver-less scope/util fns); an EXTENSION inline
				// op (count/filter/let/also) instead CALLs its now-correctly-routed rt method (the ref splice body uses the
				// Kotlin iterator protocol -> unresolved under substitution, but the rt body iterates via the fixed
				// forEachInline). Non-local returns through an ext-inline lambda were already call-only pre-session.
				if (callee.isInline && hasLambdaArg(call) && extRecv == null) return inlineSpliceCall(call, fileClass)
				// An extension fun: its receiver is the .NET method's first param (`__self`), so prepend it to the args.
				val a = listOfNotNull(extRecv) + filledArgExprs(call)   // fill omitted default args (trailing/named-middle/reordered)
				// A GENERIC top-level fun (e.g. a `reified` inline restored as a generic method) -> a generic static
				// call carrying the type args, so ilemit MakeGenericMethods it (the reified `typeof(T)`/`is T` body
				// then sees the concrete type). CLR generics are reified, so no inlining is needed across assemblies.
				if (callee.typeParameters.isNotEmpty()) {
					val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
					if (targs.all { it != null }) {
						val taJson = targs.joinToString(",") { birType(it!!).toJson() }
						// `shapeTypes` must line up with `a` (= extension receiver, then regular args), so a GENERIC extension
						// fun's `__self` receiver type is included — else bir2cir's by-shape overload pick finds 0 params.
						// PURE-KOTLIN `birType` identities; bir2cir derives the ilemit `shapes` tokens (see the member path above).
						val shapeParams = (if (extRecv != null) listOf(callee.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }) else emptyList()) + regularParams(callee)
						val shapeTypes = shapeParams.joinToString(",") { birType(it.type).toJson() }
						// A2 (#61): a PLAIN static call by identity carrying the generic facts (typeArgs + shapeTypes);
						// bir2cir's NetInteropBinding resolves the file-class owner off the refs and shapes it to clrGenericStatic.
						return """{"k":"callStatic","ownerType":${str(fileClass)},"method":${str(name)},"typeArgs":[$taJson],"shapeTypes":[$shapeTypes],"args":[${a.joinToString(",") { expr(it) }}]${suspendCallTag(callee)}}"""
					}
				}
				// A2 (#61): a PLAIN static call by identity to the referenced .NET file class; bir2cir's NetInteropBinding
				// shapes it to clrStatic. A `suspend` callee is flagged by `suspendCallTag` (Task/await lowering deferred).
				val ret = birType(callee.returnType)
				return """{"k":"callStatic","ownerType":${str(fileClass)},"method":${str(name)},"argTypes":[${a.joinToString(",") { birType(it.type).toJson() }}],"ret":${str(ret)},"args":[${a.joinToString(",") { expr(it) }}]${suspendCallTag(callee)}}"""
			}
		}
		// Fill omitted constant default arguments at the call site (IL methods have no default mechanism).
		val args = filledArgs(call).joinToString(",")
		// A generic method `fun <T> id(...)` -> carry the resolved type args so ilemit can MakeGenericMethod.
		val ta = typeArgsJson(call)
		// PLAIN Kotlin return type for the retType hint; a `suspend` callee is flagged by `suspendCallTag` on the node
		// (the kickoff/Task/await lowering is a deferred downstream layer). kotc bakes no coroutine ABI here.
		val effRet = birType(call.type)
		val recv = dispatchReceiver(call)
		// An extension function: the receiver is the `__self` first arg. TOP-LEVEL `fun T.f()` -> static `f(self,args)`.
		// MEMBER `class C { fun T.f() }` has BOTH receivers -> instance method on the enclosing C (dispatch receiver),
		// with the extension receiver as the first arg (mirrors the JVM `C.f(T $receiver)` shape).
		val extRecv = extensionReceiver(call)
		if (extRecv != null) {
			val all = (listOf(expr(extRecv)) + filledArgs(call)).joinToString(",")
			if (recv != null) {
				val ownerStr = ownerSpec(declaringClass, recv.type)
				val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
				return """{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${expr(recv)},"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$all]${suspendCallTag(callee)}}"""
			}
			return """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$all]${suspendCallTag(callee)}}"""
		}
		// Instance method on a user class, or a sibling top-level call.
		return if (recv != null) {
			// `it.hasNext()`/`it.next()` on a Kotlin iterator, `xs.iterator()` on a Kotlin iterable dispatch on the REAL
			// generic identity via ownerSpec below (`kotlin.collections.Iterator[int]` / `Iterable[int]`) — bir2cir
			// substitutes/normalizes them (no monomorphized synthetic; #58).
			val ownerStr = ownerSpec(declaringClass, recv.type)
			val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
			// A call to an override of a .NET-mapped interface member (e.g. a user Continuation's resumeWith) uses
			// the .NET member name (ResumeWith), matching what the class emitted.
			val mname = clrIfaceMemberName(callee) ?: objectMethodName(callee) ?: name
			// Carry the return type so ilemit can fall back to dynamic dispatch if static resolution fails AND the owner
			// implements a BCL clrg: interface (a substituted Kotlin collection whose member -- get_Item, iterator, addAll
			// -- lives on the BCL interface FindMethod skips). ilemit gates on the owner-interface so non-collection misses
			// still throw. See ilemit EmitDynamicCall.
			val dynRet = ""","dynRet":${birType(call.type).toJson()}"""
			"""{"k":"callInstance","ownerType":${ownerStr.toJson()},"virtual":$virtual,"recv":${recvExpr(recv, ownerStr, declaringClass?.defaultType)},"method":${str(mname)}${overloadSigField(callee)}$ta$dynRet${retHintStr(ta.isNotEmpty() || (ownerStr as? TypeNode.Fqn)?.args != null, effRet)},"args":[$args]${suspendCallTag(callee)}${overridesJson(callee)}}"""
		} else """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$args]${suspendCallTag(callee)}}"""
	}

	/**
	 * `,"ret":${fqnJson("kotlin.Int")}` for a generic call/member access: the concrete result type is known here (FIR-resolved
	 * `call.type`), so ilemit need not reflect the un-baked builder's return type (which stays `!0`/`!!0` and
	 * would mis-drive value-type boxing). Only emitted for the generic/constructed paths to stay non-invasive.
	 */
	internal fun retHint(generic: Boolean, t: IrType): String =
		if (generic) ""","ret":${birType(t).toJson()}""" else ""

	/** Like [retHint] but with a pre-computed return-type string (e.g. a suspend call's kickoff `Task<T>`). */
	internal fun retHintStr(generic: Boolean, ret: TypeNode): String =
		if (generic) ""","ret":${ret.toJson()}""" else ""

	/** Neutral metadata tag marking a call whose callee is a `suspend` function. kotc records only the FACT
	 *  (mirroring the `"suspend":true` fn-decl flag); the coroutine LOWERING (await / state machine / Task ABI)
	 *  is a DEFERRED downstream layer that consumes this tag. kotc does NO coroutine lowering. */
	internal fun suspendCallTag(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
		if ((callee as? IrSimpleFunction)?.isSuspend == true) ""","suspendCall":true""" else ""

	/** `,"typeArgs":["int"]` when the callee is a generic method (its own type params resolved at this call). */
	internal fun typeArgsJson(call: IrCall): String {
		val tps = call.symbol.owner.typeParameters
		if (tps.isEmpty()) return ""
		val args = tps.indices.map { call.typeArguments.getOrNull(it) }
		if (args.any { it == null }) return ""
		return ""","typeArgs":[${args.joinToString(",") { birType(it!!).toJson() }}]"""
	}

	/**
	 * The .NET TYPE name for an S5 FIR-injected .NET type (synthesized into FIR without annotations): read off the injected
	 * symbol's RESOLVED IR identity — the type's `ClassId` (`kotc.frontend.clrInjectedDotNetName`), a structural projection
	 * of facadegen's metadata (A2 interop-no-registry, stage 1 — no injector-populated name-keyed side-table). The backend
	 * must resolve this so injected types are real .NET types (otherwise they leak in as user classes). MEMBER slot names
	 * are NOT resolved here — that is bir2cir's DeclarationRename off the `overrides` marker + the refs (A2 step 5).
	 */
	internal fun clrName(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): String? {
		// TYPE IDENTITY ONLY. kotc no longer resolves any MEMBER slot name here: a facadegen-injected .NET member's slot
		// AND a stdlib @ClrTypeAlias/@ClrIntrinsic member's slot are BOTH resolved by bir2cir's DeclarationRename off the
		// `overrides` marker + the refs (A2 / #61 step 5). This accessor now yields only the .NET TYPE name for an
		// injected .NET type (read off its IR `ClassId`) — used for type-origin decisions (a `newClr`, a `clr:`/`clrg:`
		// owner token, the `byrefBackingField`/delegate/for-in origin tests), never a member slot.
		// A2 stage 1: the injected .NET type's .NET name is read straight off its IR `ClassId` (structural resolved
		// identity) against facadegen's metadata.
		return (decl as? IrClass)?.classId?.let { kotc.frontend.clrInjectedDotNetName(it) }
	}

	/** The `byref(x)` marker intrinsic wrapping an arg -> the inner lvalue `x`; else null. Matched by FULL name
	 *  (`kotlin.clr.byref`) so a user function happening to be named `byref` is not mistaken for the intrinsic. */
	internal fun byrefMarker(a: IrExpression): IrExpression? =
		if (a is IrCall && a.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.clr.byref") regularArgs(a).firstOrNull() else null

	/** A stdlib byref parameter marked `@kotlin.clr.ClrRefArgument`: its argument is passed BY REFERENCE to the bound
	 *  BCL member (bir2cir wraps the arg position `byref:` at substitution). kotc reads it ONLY to SHAPE the argument
	 *  addressably — the byref call-substitution decision itself is bir2cir's. */
	internal fun isClrRefArgument(p: IrValueParameter): Boolean =
		p.annotations.any { it.type.classFqName?.asString() == "kotlin.clr.ClrRefArgument" }

	/** Emit one regular call argument as its ADDRESSABLE lvalue (a property's backing FIELD node, else the lvalue
	 *  itself) when the matching callee parameter is byref, so ilemit's EmitArg(want.IsByRef) can `ldflda`/`ldloca` it.
	 *  Two byref shapes: a USER `ClrRef<T>` param (`byref:`) unwraps its explicit `byref(x)` marker; a STDLIB
	 *  `@ClrRefArgument` param (a PLAIN type, no marker) shapes the bare arg directly — the stdlib's @ClrIntrinsic
	 *  Interlocked/TryParse/DivRem helpers, plain calls in the ref build, substituted to BCL `ref`/`out` calls by
	 *  bir2cir in the rt build. A non-byref parameter is unaffected (inert for every existing call). */
	internal fun argExpr(arg: IrExpression, param: IrValueParameter?): String {
		if (param != null) {
			if (birType(param.type) is TypeNode.ByRef) byrefMarker(arg)?.let { inner ->
				return byrefBackingField(inner) ?: expr(inner)
			}
			else if (isClrRefArgument(param)) return byrefBackingField(arg) ?: expr(arg)
			// A value-type-nullable arg (`Int?` smart-cast to `Int`) passed to a non-null value param must UNWRAP
			// `Nullable<T>.Value` — the CLR twin of JVM's implicit `Integer.intValue()` arg coercion (no IR node). C1.
			if (!isPreUnwrappedRead(arg)) nullableValueUnwrapElem(arg.type, param.type)?.let { elem ->
				return """{"k":"nullableValue","elem":${str(elem)},"e":${expr(arg)}}"""
			}
			// A boxed Any operand (an un-narrowed smart-cast, `x is Int && f(x)`) passed to a concrete value-primitive
			// param -> cast to the param type so the VALUE, not the box, reaches the slot. This is the arg twin of
			// recvExpr's boxed-Any coercion: a primitive operator (`a + b`) lowered by bir2cir flows its arg through here.
			if (param.type.isPrimitiveOrUnsigned() && birType(arg.type) == OBJ)
				return """{"k":"cast","type":${str(birType(param.type))},"e":${expr(arg)}}"""
		}
		return expr(arg)
	}

	/** Read the RECEIVER of a member call on a value-type primitive as its BARE VALUE: a value-nullable (`Int?`)
	 *  smart-cast surfaces `Nullable<T>.Value`; a boxed `Any` smart-cast casts to the primitive. The receiver-slot
	 *  twin of [argExpr]'s value coercion — a member call on `kotlin.Int`/`kotlin.Char`/… (a primitive
	 *  operator, `compareTo`, `toString`, …) needs the raw value, not a `Nullable<T>` struct load / a box. A no-op
	 *  for any non-primitive owner. */
	internal fun recvExpr(recv: IrExpression, ownerType: TypeNode, ownerIr: IrType?): String {
		// The owner's value-primitive-ness is read from the IR (`ownerIr` = the member's declaring class, or the
		// receiver's own type when the receiver was boxed to Any) — no kotlin.* primitive FQN table.
		val ownerPrim = ownerIr?.isPrimitiveOrUnsigned() == true || recv.type.isPrimitiveOrUnsigned()
		if (!ownerPrim || isPreUnwrappedRead(recv)) return expr(recv)
		nullableElem(recv.type)?.let { elem -> return """{"k":"nullableValue","elem":${str(elem)},"e":${expr(recv)}}""" }
		if (birType(recv.type) == OBJ) return """{"k":"cast","type":${str(ownerType)},"e":${expr(recv)}}"""
		return expr(recv)
	}
	
	/** A `byref(...)` target that is an own-source-set property read -> its BACKING-FIELD node, so ilemit takes the
	 *  field address (`ldflda <backing>`) instead of addressing an accessor's return value (Phase 5). The field is
	 *  INTERNAL, hence reachable across types in-module. Null for a non-property, a .NET/injected property, or a
	 *  computed/delegated/lateinit/@ClrField property (no plain in-module backing field to address). */
	internal fun byrefBackingField(inner: IrExpression): String? {
		val call = inner as? IrCall ?: return null
		val callee = call.symbol.owner
		val prop = callee.correspondingPropertySymbol?.owner ?: return null
		if (callee !== prop.getter) return null
		val cls = callee.parent as? IrClass ?: return null
		if (clrName(cls) != null) return null
		if (prop.backingField == null || prop.isDelegated || prop.isLateinit || isClrField(prop)) return null
		val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
		val owner = ownerSpec(cls, dispatchReceiver(call)?.type).toJson()
		return """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(prop.name.asString())}}"""
	}

	/** (argsJson, argTypesJson) for an injected .NET call. A `ClrRef<T>` param already maps to `byref:T` via birType
	 *  (so the out/ref overload resolves + optional params still default-fill); a `byref(x)` arg unwraps to its lvalue
	 *  `x`, which ilemit passes by address (EmitArg routes an IsByRef param through EmitAddr). */
	internal fun clrCallArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression, callee: org.jetbrains.kotlin.ir.declarations.IrFunction): Pair<String, String> {
		val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
		val tj = params.map { birType(it.type).toJson() }
		val aj = regularArgs(call).map { val inner = byrefMarker(it); if (inner != null) (byrefBackingField(inner) ?: expr(inner)) else expr(it) }
		return aj.joinToString(",") to tj.joinToString(",")
	}

	internal fun constJson(c: IrConst): String = when (val v = c.value) {
		is String -> str(v)
		is Boolean -> v.toString()
		is Char -> str(v.toString())
		null -> "null"
		// NaN / ±Infinity are not valid JSON number tokens (`{"value":NaN}` breaks the parser) — emit them as a string
		// the ilemit const handler decodes to the special double/float (`Double.NaN` etc. appear in stdlib `average()`).
		is Double -> if (v.isNaN() || v.isInfinite()) str(v.toString()) else v.toString()
		is Float -> if (v.isNaN() || v.isInfinite()) str(v.toString()) else v.toString()
		else -> v.toString()
	}

	/** True if `t` is an array kotc emits array intrinsics (arrayGet/arraySet/arrayLen/forArray) for: a reference
	 *  `Array<T>`, a signed primitive array, or an unsigned specialized array (`UByteArray`/…, #76 — native like
	 *  signed, in ALL builds). Array-ness is read from the IR type system
	 *  (`isBoxedArray`/`isPrimitiveArray`/`isUnsignedArray`), NOT a kotlin.* FQN table. */
	internal fun isArrayType(t: IrType): Boolean =
		t.isBoxedArray || t.isPrimitiveArray() || t.isUnsignedArray()

	/** A value-type primitive (`kotlin.Int`/`Char`/`Boolean`/…, the 8 the CLR represents as a struct) — read from
	 *  the IR type system (`isPrimitiveType`), NOT a kotlin.* FQN table. Nullability-agnostic. */
	internal fun IrType.isValuePrimitive(): Boolean = makeNotNull().isPrimitiveType()
	/** A value-type primitive OR an unsigned inline-class primitive (`kotlin.UInt`/…) — the set whose operators
	 *  lower to raw CIL / whose receiver+args need value coercion. Read from the IR, not a FQN table. */
	internal fun IrType.isPrimitiveOrUnsigned(): Boolean = makeNotNull().let { it.isPrimitiveType() || it.isUnsignedType() }

	/** The element type of a REFERENCE `Array<T>`. NOT called for a signed OR unsigned specialized primitive array —
	 *  bir2cir DERIVES that element off the faithful `kotlin.IntArray`/`kotlin.UByteArray`/… identity. */
	internal fun arrayElemType(t: IrType): TypeNode {
		val fq = t.classFqName?.asString()
		if (fq == "kotlin.Array")
			return (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: OBJ
		return OBJ
	}

	/** Kotlin nullable VALUE type (`Int?`/`Double?`…) -> the value element identity (`kotlin.Int`…), else null. */
	internal fun nullableElem(t: IrType): TypeNode? =
		if (t.isMarkedNullable() && t.isValuePrimitive()) t.classFqName?.asString()?.let { TypeNode.Fqn(it) } else null

	/** A value-type-nullable source (`Int?` = `Nullable<T>` on the CLR) narrowed/cast to its NON-null value
	 *  (`Int`) must read `Nullable<T>.get_Value` — a bare load / `unbox.any` over a `Nullable<T>` STRUCT reads
	 *  garbage or emits invalid IL (the C1 smart-cast miscompile). Given the SOURCE and required non-null USE/target
	 *  type, returns the element to wrap in a `nullableValue` unwrap, else null. */
	internal fun nullableValueUnwrapElem(srcType: IrType, useType: IrType): TypeNode? {
		val elem = nullableElem(srcType) ?: return null          // source is Int?/Long?/Double?…
		if (useType.isMarkedNullable()) return null              // target is still nullable -> no unwrap
		val tgt = useType.classFqName?.asString()?.takeIf { useType.isValuePrimitive() } ?: return null
		return if (elem is TypeNode.Fqn && tgt == elem.name) elem else null
	}

	/** Emit `node` coerced into a slot of the EXPECTED type: unwrap a value-type-nullable (`Int?`) to its
	 *  non-null value (`Int`) when the slot demands the bare value — the CLR twin of the JVM backend's implicit
	 *  `Integer.intValue()` coercion at an assignment / argument / return, which has NO IR cast node. */
	internal fun coerceValue(node: IrExpression, expected: IrType): String =
		if (isPreUnwrappedRead(node)) expr(node)
		else nullableValueUnwrapElem(node.type, expected)?.let { elem -> """{"k":"nullableValue","elem":${str(elem)},"e":${expr(node)}}""" } ?: expr(node)

	/** True if reading `o` already yields the bare non-null VALUE of a value-type-nullable — an IrGetValue whose
	 *  `valSubst` substitution was pre-unwrapped to `Nullable<T>.Value` (a `SAFE_CALL` receiver). The unwrap helpers
	 *  must then NOT wrap again, or the `.Value` is read twice (`n?.plus(1)` -> 1 instead of 8). */
	internal fun isPreUnwrappedRead(o: IrExpression): Boolean =
		o is IrGetValue && o.symbol.owner.name.asString() in valSubstUnwrapped

	/** Kotlin visibility -> BIR access keyword (public/private/internal/protected). */
	internal fun visOf(d: IrDeclarationWithVisibility): String = when (d.visibility.delegate) {
		Visibilities.Private, Visibilities.PrivateToThis -> "private"
		Visibilities.Internal -> "internal"
		Visibilities.Protected -> "protected"
		else -> "public"
	}

	/** A Kotlin `Any`-override -> its System.Object method name (`toString`->`ToString`…), else null. */
	internal fun objectMethodName(fn: IrSimpleFunction): String? {
		// Only a REAL instance-member override maps to the System.Object name. A top-level / EXTENSION function named
		// `hashCode`/`toString` (e.g. `Any?.hashCode()`, `Any?.toString()`) is NOT an Object override — renaming it to
		// GetHashCode/ToString makes a STATIC method on the file class collide with the inherited Object.<Name> slot
		// (TypeLoad "do not match", e.g. HashCodeKt/LibraryKt). Require a dispatch receiver + no extension receiver.
		val hasDispatch = fn.parameters.any { it.kind == IrParameterKind.DispatchReceiver }
		val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
		if (!hasDispatch || hasExt) return null
		val reg = fn.parameters.count { it.kind == IrParameterKind.Regular }
		return when (fn.name.asString()) {
			"toString" -> if (reg == 0) "ToString" else null
			"hashCode" -> if (reg == 0) "GetHashCode" else null
			"equals" -> if (reg == 1) "Equals" else null
			else -> null
		}
	}


	// kotc emits ONLY the faithful op + faithful operand expression nodes — no cast-stripped static-TYPE HINTS
	// (stripImplicit/stripCast); bir2cir (StaticType / StaticTypeResolver.cs) recovers each operand's static
	// type STRUCTURALLY off the emitted node + a local/param type environment — the single uniform source for the
	// collection/float/toString/nullable Kotlin-semantic recognition (those helpers live in bir2cir).

	// The erased / star-projection / Any? fallback type identity. kotc emits the pure Kotlin FQN `kotlin.Any`;
	// bir2cir/ilemit resolve it to System.Object. (Replaces the old bare-string `object` shorthand.)
	internal val OBJ: TypeNode get() = TypeNode.Fqn("kotlin.Any")

	/** Structured-Type JSON for a bare Kotlin/synthetic FQN identity — the ONLY way a type reaches the wire.
	 *  Used to spell a KNOWN type-literal (a `kotlin.*` primitive, a `dotkt$*` synthetic) in a hand-built node
	 *  template: `"type":${fqnJson("kotlin.Int")}` (never a bare string). */
	internal fun fqnJson(name: String): String = TypeNode.Fqn(name).toJson()

	/** True if a structured type contains a type variable (`tv`) anywhere — replaces the `.contains("gp:")` scan.
	 *  A non-generic synthetic (`dotkt_KProperty`) can't bake a `tv`, so this gates the fall-through. */
	internal fun hasTv(t: TypeNode): Boolean = when (t) {
		is TypeNode.Tv -> true
		is TypeNode.Fqn -> t.args?.any { hasTv(it) } == true
		is TypeNode.Fn -> hasTv(t.ret) || t.params.any { hasTv(it) } || (t.recv?.let { hasTv(it) } == true)
		is TypeNode.Nullable -> hasTv(t.of)
		is TypeNode.Oblivious -> hasTv(t.of)   // frontend-only, but keep the match exhaustive
		is TypeNode.Array -> hasTv(t.elem)
		is TypeNode.ByRef -> hasTv(t.of)
	}

	/** A collision-free identifier fragment derived from a structured type's canonical JSON (interim; the §2.4
	 *  registry replaces this). Non-alnum chars collapse to `_`, so distinct `Type`s stay distinct (via toJson). */
	internal fun mangle(t: TypeNode): String = t.toJson().replace(Regex("[^A-Za-z0-9]"), "_")

	/**
	 * A type parameter -> a POSITIONAL, scope-tagged `tv` (spec §1). scope="method" (CLR `!!i`) when declared on
	 * a function/constructor (i = its index in the method's own type params); scope="type" (CLR `!i`) when declared
	 * on a class (i = the FLATTENED index over the enclosing-type nesting chain — enclosing params prepended, matching
	 * the `enclArgs + ownArgs` construction order everywhere else). bir2cir/ilemit derive the CLR generic parameter.
	 */
	internal fun tvOf(param: IrTypeParameter): TypeNode.Tv {
		val decl = param.parent
		return if (decl is IrClass) TypeNode.Tv("type", innerEnclosingTypeParams(decl).size + param.index)
		else TypeNode.Tv("method", param.index)
	}

	/** birType of a type-argument at index [i], or null if absent/non-projection. */
	private fun argType(t: IrType, i: Int): TypeNode? =
		(t as? IrSimpleType)?.arguments?.getOrNull(i)?.let { (it as? IrTypeProjection)?.type?.let(::birType) }

	/** A type-argument's identity. `birType` now UNIFORMLY wraps any nullable core (incl. a nullable type-PARAMETER
	 *  `Iterable<T?>` inner `T?`) as `{t:nullable,of:...}`, so this is just `birType`; kept as a named seam for the
	 *  call sites that document the "preserve nullable type-arg" intent. bir2cir erases the marked arg. */
	internal fun argElemNullable(at: IrType): TypeNode = birType(at)

	internal fun birType(t: IrType): TypeNode {
		// UNIFORM nullability: any `T?` -> `{t:nullable,of:<non-null core>}`, for VALUE, REFERENCE, and type-variable
		// types alike (spec §1). kotc stays CLR-free — it does NOT distinguish struct from ref; nullability rides the
		// type node only, and the decl-level `nullable`/`retNullable` flags are RETIRED. bir2cir DERIVES the CLR form
		// (value `Nullable<T>` vs reference NRT byte) from this node. Wrapping the non-null core makes every early-return
		// special case below (ClrRef/Span/array/fn/Comparator/charSeq/coroutine-fn/type-parameter) apply to the core.
		if (t.isMarkedNullable()) return TypeNode.Nullable(birType(t.makeNotNull()))
		// A type parameter `T` -> a positional `tv` (resolved in IL context). On the CLR generics are reified, so
		// even `reified T` rides on this (no inlining) — see [[clr-not-jvm-discard-jvmisms]].
		(t.classifierOrNull as? org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol)?.let { tp ->
			// While splicing an `inline fun`'s body, its OWN type params are substituted with the call's type arguments.
			typeArgSubst[tp.owner]?.let { return it }
			return tvOf(tp.owner)
		}
		// The intrinsic `kotlin.clr.ClrRef<T>` -> `byRef T` (a managed reference).
		if (t.classFqName?.asString() == "kotlin.clr.ClrRef")
			return TypeNode.ByRef(argType(t, 0) ?: OBJ)
		// The intrinsic `kotlin.clr.Span<T>` -> the FAITHFUL `kotlin.clr.Span<T>` identity. Substituting it to the
		// real `System.Span<T>` is a CLR-representation decision (the last naked `System.*` name in kotc), so bir2cir
		// owns it (LowerType), exactly like every other @ClrTypeAlias / primitive substitution.
		if (t.classFqName?.asString() == "kotlin.clr.Span")
			return TypeNode.Fqn("kotlin.clr.Span", listOf(argType(t, 0) ?: OBJ))
		// A reference array `kotlin.Array<E>` -> `TypeNode.Array(<E>)` (the element rides its own faithful identity).
		if (t.isBoxedArray) return TypeNode.Array(arrayElemType(t))
		// A SIGNED primitive array (`kotlin.IntArray`/…) OR an unsigned specialized array (`kotlin.UByteArray`/…, #76)
		// -> the FAITHFUL FQN identity (the type's OWN FQN, read from the IR — not a kotlin.* table); deciding
		// "IntArray/UByteArray IS an array of Int/UByte" is a REPRESENTATION decision that belongs in bir2cir (it
		// decomposes this token to `Array(elem)`). The array intrinsics (arrayGet/arraySet/forArray) + the sized ctor
		// are likewise bir2cir-derived off it. Unsigned mirrors signed exactly, in ALL builds (no build-mode gate).
		if (t.isPrimitiveArray() || t.isUnsignedArray()) return TypeNode.Fqn(t.classFqName!!.asString())
		val fqp = t.classFqName?.asString()
		// kotlin.text.Regex stays its bare `kotlin.*` FQN here (falls through to the user-class `@kotlin.text.Regex`
		// path below); bir2cir substitutes it to System.Text.RegularExpressions.Regex off the stdlib's @ClrTypeAlias
		// on the Regex class (metadata-driven — layer purity, no CLR name in kotc).
		// NOTE: kotlin.text.MatchResult is a REAL emitted Kotlin interface (runtime/stdlib/.../MatchResult.kt) with a real
		// CLR realization (ClrMatchResult over a System...Match); it must NOT be aliased to System...Match here — doing so
		// made `ClrMatchResult : MatchResult` try to implement a CLASS as an interface (TypeLoadException). A MatchResult
		// reference resolves as a referenced stdlib type (ilemit's MapType referenced-type fallback).
		// Kotlin throwables stay their bare `kotlin.*` FQN here (emitted as `@kotlin.IllegalArgumentException`, etc. via
		// the user-class fall-through below); bir2cir lowers them to the BCL base off the stdlib's @ClrTypeAlias on each
		// exception class (metadata-driven). A custom `class E : Exception(msg)`
		// supertype rides the same path; `.message`/`.cause` are plain property reads that bir2cir substitutes to
		// clrPropGet System.Exception.Message/.InnerException off the @ClrProperty binding (no kotc BCL-name knowledge).
		// kotlin.AutoCloseable (and its jar typealias kotlin.io.Closeable) stays its bare `kotlin.*` FQN here (falls
		// through to the user-class `@kotlin.AutoCloseable` path below); bir2cir substitutes it to System.IDisposable off
		// the stdlib's @ClrTypeAlias binding (layer purity — no CLR type name in kotc). The `close()->Dispose` member
		// rename + the `use{}` finally call are likewise metadata-driven (@ClrIntrinsic("Dispose")).
		// kotlin.CharSequence -> a synthetic interface (no faithful .NET equivalent). See charSeqIface.
		charSeqIface(t)?.let { return TypeNode.Fqn(it) }
		// A function type as a value (e.g. a `(P)->R` parameter): `kotlin.FunctionN` -> `fn` (Func/Action shape). A
		// `kotlin.coroutines.SuspendFunctionN` (a `suspend (P)->R` value) sets `fn.suspend=true` — the SAME delegate
		// shape carrying the suspend FACT (which the newSuspendLambda SM builder needs). bir2cir ERASES a suspend `fn`
		// to `object` wherever it lands in a TYPE slot (only the `funcType` node key keeps it), so kotc bakes no
		// coroutine ABI here — behavior-preserving.
		if (fqp != null && (fqp.startsWith("kotlin.coroutines.SuspendFunction") || fqp.startsWith("kotlin.Function"))) {
			val args = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
			if (args.isNotEmpty()) {
				val ret = args.last(); val ps = args.dropLast(1)
				val suspend = fqp.startsWith("kotlin.coroutines.SuspendFunction")
				return TypeNode.Fn(suspend, funcRetTypeOf(ret), ps.map { birTypeDeleg(it) })
			}
		}
		// `by lazy` delegate: kotlin.Lazy<T> is a REAL emitted stdlib interface (its impl `UnsafeLazyImpl` is pure
		// Kotlin, produced by the stdlib `lazy()` function) — kotc emits the plain Kotlin type identity and falls
		// through to the user-class/interface branch below (`@kotlin.Lazy[…]`). It is NOT aliased to System.Lazy:
		// that CLR type is SEALED, so a Kotlin class could not implement it, and the alias was pure CLR knowledge
		// that must not live in kotc (layer purity — cf. coerce/isBlank pure-body migration).
		// kotlin.reflect.KProperty0/KMutableProperty0/KProperty1/KMutableProperty1 (a `::prop` callable reference's
		// type, and the compiler-synthesized KProperty argument of a delegate's getValue/setValue) are REAL emitted
		// stdlib interfaces (KPropertyClr.kt) — falls through to the user-class/interface branch below
		// (`kotlin.reflect.KProperty0[…]` etc.), the SAME real-generic-stdlib-interface path as `kotlin.Lazy<T>` above.
		// kotc's `propertyRef`/`kPropertyStub` materialize REAL implementations of these interfaces (#70) — no more
		// synthetic `dotkt$KProperty` name-bag identity here.
		// kotlin.properties.Read(Write)Property<T,V> is NOT monomorphized: it falls through to the
		// user-class/interface branch below (`@kotlin.properties.ReadWriteProperty[…]`), the REAL generic
		// stdlib interface — same as `by lazy`'s `kotlin.Lazy<T>`. A delegate field/local typed as this
		// interface, the value from `Delegates.observable(…)`, and the getValue/setValue dispatch owner then
		// all agree on one type identity (ilverify-clean). The real generic interface is used (as generic
		// `kotlin.Lazy<T>` works with a value V).
		// Kotlin function type `(A,B)->R` (kotlin.FunctionN<A,B,R>) and a callable-reference type `KFunctionN<…>`
		// (the inferred type of `obj::method`/`::foo`) -> an `fn` (Func/Action delegate).
		val fqn = t.classFqName?.asString()
		if (fqn != null && (fqn.startsWith("kotlin.Function") || fqn.startsWith("kotlin.reflect.KFunction"))) {
			val tys = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
			if (tys.isNotEmpty()) {
				val retT = tys.last()
				val ret = if (retT.isUnit()) TypeNode.Fqn("kotlin.Unit") else argElemNullable(retT)
				return TypeNode.Fn(false, ret, tys.dropLast(1).map { birType(it) })
			}
		}
		// kotc emits the Kotlin FQN identity as-is for a SOURCE TYPE — it knows nothing about the CLR. bir2cir lowers
		// these (kotlin.Int -> System.Int32, kotlin.Unit -> System.Void, …). NO `int`/`void`/`System.Int32` here.
		when (val kfq = t.classFqName?.asString()) {
			"kotlin.Unit", "kotlin.Nothing", "kotlin.Any",
			"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
			"kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char", "kotlin.String",
			"kotlin.UInt", "kotlin.ULong", "kotlin.UByte", "kotlin.UShort" -> return TypeNode.Fqn(kfq)
		}
		// A (Mutable)Iterator<E>/Iterable<E> — for ANY element E, type-parameter (`gp:E`) or concrete (`int`) alike —
		// maps to the REAL generic identity via the FQN path below: `kotlin.collections.Iterator[E]` (a real emitted
		// stdlib interface) / `Iterable[E]` (bir2cir @ClrTypeAlias'd to `System.Collections.Generic.IEnumerable<E>`).
		// kotc produces no per-element monomorphized synthetic: the reverse GetEnumerator bridge in ilemit + the real
		// generic Iterator interface handle the IEnumerable<->Iterator read-as (roadmap step 3).
		val klass = t.classifierOrNull?.owner as? IrClass
		// A @Clr / FIR-injected .NET type ("clr:System.Text.StringBuilder"); a constructed generic .NET type
		// (`Collection<Int>`) carries its concrete args as `clrg:<openName>[int]`.
		val clrTypeParams = klass?.typeParameters
		klass?.let { clrName(it) }?.let { netName ->
			// A .NET-injected / stdlib type identity: emit its Kotlin FQN (`netName`) as an `fqn`. bir2cir/ilemit
			// resolve whether it is a referenced .NET type / generic (the old `clr:`/`clrg:` decision). A nested
			// nullable type-parameter arg keeps its `nullable(tv)` marker (bir2cir erases it).
			val args = (t as? IrSimpleType)?.arguments?.mapNotNull { arg ->
				(arg as? IrTypeProjection)?.type?.let { argElemNullable(it) }
			}
			return when {
				!args.isNullOrEmpty() -> TypeNode.Fqn(netName, args)
				// A GENERIC type referenced raw / star-projected (no args) still needs its arity — fill `object` per
				// type param (the open generic def is unresolvable downstream).
				!clrTypeParams.isNullOrEmpty() -> TypeNode.Fqn(netName, clrTypeParams.map { OBJ })
				else -> TypeNode.Fqn(netName)
			}
		}
		// Enums -> the real .NET enum type reference (package-qualified, like other user types).
		if (klass != null && klass.kind == ClassKind.ENUM_CLASS) return TypeNode.Fqn(typeName(klass))
		// A user-declared class/interface becomes a reference to that BIR type; a constructed user generic carries
		// concrete args. Anon objects resolve through `typeName`.
		if (klass != null && (klass.kind == ClassKind.CLASS || klass.kind == ClassKind.INTERFACE)) {
			// An `inner class` re-declares its enclosing class(es)' type params; reference it WITH those (as `tv`).
			val enclArgs = innerEnclosingTypeParams(klass).map { tvOf(it) }
			if (klass.typeParameters.isNotEmpty()) {
				val sargs = (t as? IrSimpleType)?.arguments
				if (!sargs.isNullOrEmpty()) {
					val ownArgs = sargs.map { a ->
						val at = (a as? IrTypeProjection)?.type
						when {
							// A STAR projection (`Comparable<*>`) -> Any (dropping it leaves a raw generic — malformed).
							at == null -> OBJ
							// A `Unit` TYPE-ARG stays the real Unit identity (a generic arg of System.Void is invalid).
							at.isUnit() -> TypeNode.Fqn("kotlin.Unit")
							// A NULLABLE type-parameter arg keeps its `nullable(tv)` marker (bir2cir erases it).
							else -> argElemNullable(at)
						}
					}
					return TypeNode.Fqn(typeName(klass), enclArgs + ownArgs)
				}
			}
			if (enclArgs.isNotEmpty()) return TypeNode.Fqn(typeName(klass), enclArgs)
			return TypeNode.Fqn(typeName(klass))
		}
		return OBJ
	}

	/** JSON for a structured type in a node template — `str(typeNode)` emits the `{t:…}` object (no quotes).
	 *  An overload of `str(String)` so every `"type":${str(x)}` site works whether x is a name or a Type. */
	internal fun str(t: TypeNode): String = t.toJson()

	internal fun str(s: String): String {
		val escaped = buildString(s.length + 2) {
			for (ch in s) {
				when (ch) {
					'\\' -> append("\\\\")
					'"' -> append("\\\"")
					'\n' -> append("\\n")
					'\r' -> append("\\r")
					'\t' -> append("\\t")
					'\b' -> append("\\b")
					'\u000C' -> append("\\f")
					else -> {
						if (ch.code < 0x20) {
							append("\\u")
							append(ch.code.toString(16).padStart(4, '0'))
						} else {
							append(ch)
						}
					}
				}
			}
		}
		return "\"$escaped\""
	}

}
