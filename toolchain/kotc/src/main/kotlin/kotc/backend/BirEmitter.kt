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
import org.jetbrains.kotlin.ir.expressions.IrPropertyReference
import org.jetbrains.kotlin.ir.expressions.IrFunctionExpression
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
import org.jetbrains.kotlin.ir.IrBuiltIns
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
// [irBuiltIns] is the module's IrBuiltIns (from Fir2IrActualizedResult) — needed by the type system to compute an
// inherited member inline fn's owning-class instantiation for F2A (correspondingSupertypeInstantiation). Nullable
// so a bare `BirEmitter()` still constructs; the F2A supertype path no-ops (falls back to the status-quo omit) when null.
class BirEmitter(internal val messageCollector: MessageCollector? = null, internal val irBuiltIns: IrBuiltIns? = null) {

	// Diagnostics: a construct the .NET backend can't lower yet is a COMPILE-TIME error with source location
	// (file:line:col) — never a silent BIR node that crashes ilemit later. `hadError` fails the build.
	var hadError = false; internal set
	internal var fileEntry: IrFileEntry? = null

	internal fun locationOf(node: IrElement?): CompilerMessageLocation? {
		val fe = fileEntry ?: return null
		val off = node?.startOffset ?: return CompilerMessageLocation.create(fe.name)
		if (off < 0) return CompilerMessageLocation.create(fe.name)
		val lc = fe.getLineAndColumnNumbers(off)
		return CompilerMessageLocation.create(fe.name, lc.line, lc.column, null)
	}

	/**
	 * #112 Phase 2: the DECL-level source position, emitted as an optional `,"pos":{"f":path,"l":line,"c":col}` on a
	 * declaration node (method/type). It carries the originating `File.kt:line` down BIR → CIR so an ilemit/bir2cir emit
	 * failure (or a shared IrSanity violation) points at the source declaration instead of a bare `Type.method`
	 * breadcrumb. OPTIONAL: absent = pre-#112 behavior (a synthetic decl with no source omits it). Values are NUMBERS
	 * (not a `"file:line:col"` string) so the schema validator's bare-string check is not tripped; `pos.f` is the one
	 * string, allow-listed in verify-schema.py STR_OK. The leading comma makes it splice into a decl template's tail.
	 */
	internal fun posJson(node: IrElement?): String {
		val loc = locationOf(node) ?: return ""
		// IrFileEntry.getLineAndColumnNumbers (behind locationOf) yields 0-based line/column here; emit the
		// user-facing 1-based `File.kt:line` convention (editors, grep -n, compiler errors) so the diagnostic
		// points at the real source line. line<0 = a file-only location (no offset) -> emit just the path.
		val line = loc.line
		return if (line >= 0) ""","pos":{"f":${str(loc.path)},"l":${line + 1},"c":${loc.column + 1}}"""
			else ""","pos":{"f":${str(loc.path)}}"""
	}

	/**
	 * Report an unsupported Kotlin construct as a clear, source-located compile error and return a placeholder
	 * BIR node. The build fails (hadError), so this placeholder never reaches ilemit. `what` names the construct;
	 * `detail` is a plain-language explanation of why / what to do — NOT the word "deferred".
	 */
	// kotc emits ONE BIR independent of the ref/rt split. The ref/rt divergence (BCL substitution, the
	// kotlin.Comparable-bound + `in`-variance drops, the metadata strip) is entirely bir2cir's + ilemit's, keyed off the
	// `--build-stdlib=metadata|runtime` flag downstream. The stdlib REFERENCE build and the RUNTIME build produce
	// BIT-IDENTICAL BIR from a single kotc frontend run. docs/architecture.md.

	internal fun unsupported(node: IrElement?, what: String, detail: String): String {
		hadError = true
		messageCollector?.report(CompilerMessageSeverity.ERROR,
			"the .NET backend does not support $what yet: $detail", locationOf(node))
		return """{"k":"unsupportedExpr","of":${str("$what — $detail")}}"""
	}

	/**
	 * Report a BROKEN EMITTER INVARIANT as a source-located compile error and return a placeholder BIR node. Unlike
	 * [unsupported] this is never a statement about the language: the construct IS supported, and reaching here means
	 * the emitter's own bookkeeping is inconsistent, so the message must name the invariant rather than tell the user
	 * to rewrite working code. The build fails (hadError), so the placeholder never reaches ilemit.
	 */
	internal fun invariantBroken(node: IrElement?, invariant: String): String {
		hadError = true
		messageCollector?.report(CompilerMessageSeverity.ERROR,
			"kotc internal error: broken emitter invariant — $invariant", locationOf(node))
		return """{"k":"unsupportedExpr","of":${str("broken emitter invariant — $invariant")}}"""
	}

	// A `kotlin.clr.ClrEvent<T>` value is a compile-time-only fiction (the surfaced form of a .NET event); it may
	// appear ONLY as the receiver of `subscribe(handler)`, never be materialized as a real
	// value. This flag is set true ONLY while emitting the event member-access that is the receiver of one of those
	// ClrEvent operations;
	// a ClrEvent-typed property read seen with it FALSE is a misuse (`val e = w.Changed`) and is a compile error.
	internal var clrEventReceiverOk = false
	internal inline fun <R> asClrEventReceiver(body: () -> R): R {
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
	// #6 non-null RETURN POSTCONDITIONS: while emitting a public/protected fn whose non-null reference return is
	// contract-checked, its return-target symbol -> the NPE message JSON. stmt(IrReturn) wraps a genuine (non-spliced)
	// return VALUE targeting a registered symbol in a bind-check-throw valueBlock. A nested lambda's return targets its
	// OWN (unregistered) symbol; a non-local return targeting a registered caller is (correctly) checked. See
	// BirEmitterNullContracts.kt (returnCheckMessage) and BirEmitterStatements.kt (the IrReturn case).
	internal val postconditionReturns = HashMap<org.jetbrains.kotlin.ir.symbols.IrReturnTargetSymbol, String>()
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
	internal var inlCounter = 0
	internal var scopeCounter = 0

	/**
	 * The allocator for the `__recv` family — a LIFTED LAMBDA's receiver parameter, minted by both the lift
	 * ([kotc.backend.lambdaRecvName]) and the inline splice carrier. It takes the FRAME rather than a bare prefix, so
	 * neither caller can mint a name without declaring the scope it must be fresh in.
	 *
	 * The scope argument is the point. A minted name lands in a FLAT per-frame namespace that ilemit indexes BY NAME,
	 * and the frame also holds names the USER chose — `{ __recv0 -> this + __recv0 }` is perfectly legal Kotlin. A
	 * counter alone only keeps minted names distinct from each OTHER: the lifted method got two parameters called
	 * `__recv0`, the later declaration overwrote the earlier in ilemit's by-name index, and BOTH reads loaded the
	 * regular argument (a silently wrong value, no diagnostic).
	 *
	 * SCOPE OF THE GUARANTEE, precisely: this makes the `__recv` family fresh against its own frame. It is NOT yet a
	 * universal frame allocator — the other compiler-minted families (`__nv`, `__nn`, `__subj`, `__inlRet`, `__sbp`/
	 * `__sbl`, `__tailrec_<label>_<n>`) are still minted from a counter alone, so a user identifier spelled exactly
	 * like one of them can still alias it. That family is tracked as a known limitation, not fixed here. Names in the
	 * `dotkt$…` namespace are exempt by construction: the frontend rejects `$` in an identifier, backticks included
	 * ("name contains illegal characters"), so no source can spell one.
	 */
	internal fun freshFrameName(prefix: String, scope: IrElement?): String {
		val taken = frameNames(scope)
		while (true) {
			val candidate = "$prefix${inlCounter++}"
			if (candidate !in taken) return candidate
		}
	}

	/** Every value name VISIBLE inside `scope`: declared there (its own parameters, nested variables/parameters) AND
	 *  every name it merely READS or WRITES — i.e. its CAPTURES, which are declared in an enclosing frame but land in
	 *  the SAME emitted frame as the minted name (a closure field, a leading lift parameter, a spliced carrier
	 *  binding). Collecting only declarations is not enough and was measured to be wrong: for
	 *  `fun f(__recv0: Int) = runWith { this * 100 + __recv0 }` the lambda declares nothing, so the allocator handed
	 *  back `__recv0`, and the captured read then resolved to the receiver (201 came out as 202).
	 *
	 *  Deliberately over-approximate — a nested lambda's own parameters end up in their own lifted frame, and a read
	 *  of an unrelated outer local costs at most one skipped index. Over-approximating makes a minted name more
	 *  conservative; under-approximating makes it silently wrong. */
	private fun frameNames(scope: IrElement?): Set<String> {
		if (scope == null) return emptySet()
		val out = HashSet<String>()
		scope.acceptVoid(object : org.jetbrains.kotlin.ir.visitors.IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				when (element) {
					is org.jetbrains.kotlin.ir.declarations.IrVariable -> out.add(element.name.asString())
					is org.jetbrains.kotlin.ir.declarations.IrValueParameter -> out.add(element.name.asString())
					// A CAPTURE: read/written here, declared elsewhere, emitted into this frame.
					is org.jetbrains.kotlin.ir.expressions.IrGetValue -> out.add(element.symbol.owner.name.asString())
					is org.jetbrains.kotlin.ir.expressions.IrSetValue -> out.add(element.symbol.owner.name.asString())
					else -> {}
				}
				element.acceptChildrenVoid(this)
			}
		})
		return out
	}
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
	// #89: the owning .NET type name of a STATIC backing field — a top-level property's field lives on the file
	// class (parent is the IrFile, isStatic); a PLAIN companion property's field is emitted static on the ENCLOSING
	// class (statFields), matching how kotc flattens companion members to enclosing statics. Returns null for a
	// plain-instance backing field (a normal class or `object` property field — accessed via `this`/INSTANCE) and
	// for a SUPER-TYPED companion (a lifted concrete singleton whose members stay instance fields, not enclosing
	// statics). Reached when a property's own custom accessor body reads/writes `field` (an IrGet/SetField).
	internal fun staticBackingFieldOwner(fld: org.jetbrains.kotlin.ir.declarations.IrField): String? {
		// A `lateinit` field keeps its own null-checked read/write path (lateinitGet) — never shadow it with a plain
		// staticField load, even for a top-level/companion lateinit (defensive: its default accessors aren't emitted,
		// so this is currently unreached, but the ordering must stay lateinit-first if that ever changes). #89.
		if (fld.correspondingPropertySymbol?.owner?.isLateinit == true) return null
		val parent = fld.parent
		return when {
			parent is IrFile && fld.isStatic -> fileClassName(parent)
			parent is IrClass && parent.isCompanion && superTypedCompanion(parent.parent as IrClass) == null ->
				typeName(parent.parent as IrClass)
			else -> null
		}
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
		// A dotted MPP filename stem (`api.common.kt` → stem `api.common`) must NOT leak its dots into the file-class
		// name: ilemit's DefineType reads a dot as a namespace separator, so `Api.commonKt` would emit as
		// Namespace=<pkg>.Api / Name=commonKt and reference projection would never surface its top-level funcs
		// (cross-module `unresolved reference`, #16). Sanitize non-identifier chars to `_` (stock Kotlin does the
		// same: `AtomicFU.common.kt` → `AtomicFU_commonKt`) BEFORE capitalize+"Kt". Mirrors `synthScope`.
		stem = stem.replace(Regex("[^A-Za-z0-9]"), "_")
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
	// A function frame is keyed by DECLARATION IDENTITY in Kotlin IR, not by the source spelling of a local. Give
	// every IrVariable an unspellable, module-unique BIR slot name so no later phase has to reconstruct lexical
	// binding from JSON scopes. Value parameters retain their observable metadata/ABI names. Inline-lambda capture
	// descriptors use this same allocator, so payload materialization and ordinary bodies share one vocabulary.
	private val localSlotNames = java.util.IdentityHashMap<IrVariable, String>()
	private var localSlotCounter = 0
	internal fun localSlotName(d: IrValueDeclaration): String =
		if (d is IrVariable) localSlotNames.getOrPut(d) { "dotkt\$local${localSlotCounter++}" }
		else d.name.asString()
	// A call value BOUND by the enclosing call's evaluation plan (§2.7): the `bindRef` READ that renders it. Keyed by
	// the value EXPRESSION's identity, so every reader of that one IR node — the call's own receiver/argument slot, an
	// inner-class `new`'s enclosing-instance arg, a spliced default, a reconstructed `copy` field — reaches the ONE
	// binding through the ordinary `expr()`. Installed by [CallPlan.bindValue] and released with the plan's scope.
	internal val planReads = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.expressions.IrExpression, String>()
	// The evaluation plan of each call whose emission is in progress, keyed by the CALL node's identity — what
	// [filledArgs]/[filledInjectedArgs] append their bindings to. Scoped by [withCallPlan]; a nested call installs its
	// own, so a plan never collects another call site's values.
	internal val callPlans = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.expressions.IrExpression, CallPlan>()
	// The type-level half of the `$default` scope: while a CALLEE's default expression is rendered into a CALLER's
	// frame, this closes the callee's type parameters against the call site's instantiation. Consulted by [birType],
	// which every emitted type passes through; null everywhere else. Installed and restored around the one `expr(def)`
	// in [filledArgs] that renders a default reading the callee's own scope.
	//   A FUNCTION rather than a substitutor, because defaults NEST: a default may itself be a call that fills a
	// default of its own, and the inner frame closes against the OUTER's, which closes against the call site. Each
	// level COMPOSES (inner applied first, then whatever was already installed), so at any depth every open type
	// variable ends up closed against the outermost call site rather than against its immediate parent.
	internal var defaultTypeSubst: ((IrType) -> IrType)? = null
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
	// Active CFG loops: (loop, continueLabelId, breakLabelId). A break/continue is matched to its target by
	// loop reference identity (so `break@outer` resolves), then emitted as `goto` the right label.
	internal val cfgLoopStack = ArrayList<Triple<org.jetbrains.kotlin.ir.expressions.IrLoop, Int, Int>>()
	/** Active `tailrec` self-tail-call rewrite for the function currently being emitted. `calls` = the set of
	 *  self-calls the frontend validated as tail-recursive (identity-keyed); `startLabel` = the CFG label at the
	 *  method's entry that a tail call jumps back to (see [tailrecJump]); `fn` = the function whose params are the
	 *  loop variables. Null unless inside a `tailrec` fn body that actually has a tail self-call. */
	internal class TailrecCtx(val calls: Set<IrCall>, val startLabel: Int, val fn: IrSimpleFunction)
	internal var tailrecCtx: TailrecCtx? = null
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
	// A `kotlin.properties.Read(Write)Property<T,V>`-typed delegate is NOT monomorphized: kotc emits the REAL
	// generic stdlib interface identity (like `by lazy`'s `kotlin.Lazy<T>`), so delegate field/local types,
	// the `Delegates.observable(…)` value, and the getValue/setValue dispatch owner share one type (ilverify-clean).

	// heap ref-cell: local `var`s captured-and-mutated by a lambda / local `fun` / object expression / local class
	// are promoted to a shared `dotkt$Ref<T>{ var v }` so the mutation is visible across the capture boundary; all
	// reads/writes of such a var go through `.v`. No inline test: an inline-argument lambda is celled like any other,
	// so the decision does not depend on which call the lambda is passed to.
	// Needing a cell is a property of the VARIABLE — "something in its scope captures and WRITES it" — not of
	// the frame that happens to be emitting it. So the set is computed ONCE for the whole module ([initRefCells],
	// before any file is emitted) and is IDENTITY-keyed, which makes an entry for a declaration the tree at hand never
	// mentions inert. Every emission root therefore sees the same decision for the same variable — a method body, a
	// constructor / init block, a property or static-field initializer, a member or interface accessor, a default
	// interface method, an enum-entry argument, a `@KotlinDefault` carrier — including the paths that emit ONE
	// expression in TWO frames (a same-module omitted default is emitted both as the callee's carrier and inline at
	// the caller), where a per-frame set would disagree with itself.
	internal var refCellVars: Set<IrValueDeclaration> = emptySet()
		private set

	/** Compute the module-wide heap ref-cell set (see [refCellVars]). Called ONCE, before any file is emitted. */
	internal fun initRefCells(module: IrElement) { refCellVars = computeRefCells(module) }

	/** identity key -> (monomorphized Ref class name, element type JSON). One entry per DISTINCT cell in the file. */
	internal val refTypes = LinkedHashMap<String, Pair<String, String>>()

	/**
	 * The cell class for a var, registered once per file.
	 *
	 * The element type ALONE does not identify a cell: a type variable prints as its positional `tv`, so `T` of one
	 * class and `T` of another in the same file print identically while carrying DIFFERENT bounds. Sharing one cell
	 * between them gives it one class's bounds and instantiates it with the other's argument — rejected downstream if
	 * the bounds conflict, and a bound the argument does not satisfy if they merely differ. So the key closes over the
	 * bounds of every variable the element mentions, and a second distinct cell for the same printed element gets a
	 * suffixed name.
	 */
	internal fun refTypeName(d: IrValueDeclaration): String {
		val elem = birType(d.type)
		val elemJson = elem.toJson()
		val key = elemJson + "|" + typeVarBoundsKey(d.type)
		refTypes[key]?.let { return it.first }
		val base = "dotkt\$${synthScope}\$Ref\$" + mangle(elem)
		val taken = refTypes.values.count { (name, _) -> name == base || name.startsWith("$base\$") }
		val name = if (taken == 0) base else "$base\$$taken"
		refTypes[key] = name to elemJson
		return name
	}

	/** A stable key for the BOUNDS of every type variable [t] mentions (see [refTypeName]); empty for a closed type. */
	private fun typeVarBoundsKey(t: IrType): String {
		val seen = java.util.Collections.newSetFromMap(
			java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrTypeParameter, Boolean>())
		val parts = ArrayList<String>()
		fun walk(ty: IrType) {
			(ty.classifierOrNull as? org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol)?.let { sym ->
				if (!seen.add(sym.owner)) return
				parts.add(tvOf(sym.owner).toJson() + ":" + sym.owner.superTypes.joinToString(",") { birType(it).toJson() })
				sym.owner.superTypes.forEach(::walk)
				return
			}
			(ty as? org.jetbrains.kotlin.ir.types.IrSimpleType)?.arguments?.forEach {
				(it as? org.jetbrains.kotlin.ir.types.IrTypeProjection)?.type?.let(::walk)
			}
		}
		walk(t)
		return parts.sorted().joinToString(";")
	}
	// #52 (kotc-purity): the monomorphized heap cell `dotkt$Ref_<elem>{ var v }` is a CLR-representation synthetic.
	// kotc emits ONLY the FACT — a file-level `refTypes` registry (each cell's name + element TYPE identity) plus the
	// use-site `new`/`field`/`setField` on the cell. bir2cir's RefCellSynthesis assembles the actual trivial class
	// (single `v` field + its init ctor) into the file `types` from this registry. The element type is unrecoverable
	// from the use-site nodes alone (a bare `field .v` read carries no type), so the registry is the required fact.
	internal fun refTypesJson(): String = refTypes.values.joinToString(",") { (name, elemJson) ->
		"""{"name":${str(name)},"elem":$elemJson}"""
	}
	internal fun isRefCell(d: IrValueDeclaration) = d in refCellVars
	/** The Ref-typed base expression for a ref-cell var: its capture field inside a closure, else the local. */
	internal fun refBase(d: IrValueDeclaration) = captureSubst[d] ?: """{"k":"local","name":${str(localSlotName(d))}}"""
	/** A captured value's type as held in the closure: the Ref cell for a ref-cell var, else its plain type. */
	internal fun captureFieldType(d: IrValueDeclaration): TypeNode = if (isRefCell(d)) TypeNode.Fqn(refTypeName(d)) else birType(d.type)

	/** Local `var`s captured AND mutated across a capture boundary within [node] (-> need a heap ref-cell). The
	 *  boundaries are every class (an object expression or a local class) and every function — a lambda, whose
	 *  `IrSimpleFunction` is visited as the `IrFunctionExpression`'s child, or a LOCAL `fun`, which lifts to a static
	 *  method taking its captures as BY-VALUE params and would otherwise write its own parameter and lose the update.
	 *  A class or function that captures nothing (every top-level/member one) contributes nothing, so those arms are
	 *  inert for it — but note a local class's MEMBER is a function whose capture set is non-empty; it is redundant
	 *  rather than inert, being a subset of what the enclosing class arm already contributes. */
	private fun computeRefCells(node: IrElement): Set<IrValueDeclaration> {
		val out = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
		node.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				val caps: List<IrValueDeclaration>? = when (element) {
					is IrClass -> capturedVarsForObject(element)
					is IrSimpleFunction -> capturedVars(element)
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
		// The `byref` out/ref marker is an intrinsic consumed at its call sites (the arg becomes a `byref:` param) —
		// never emitted as a real method.
		// Only USER functions (origin DEFINED) — a consuming module's FIR also holds plugin-INJECTED top-level funs
		// (stdlib ops restored from a referenced DotKt.Stdlib, in the synthetic `__GENERATED DECLARATIONS__` file);
		// those are the library's to provide, not ours to re-emit (a re-emitted stub has no real body -> invalid IL).
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && !isExternalNetType(it) && it.name.asString() !in setOf("byref", "stackBuffer") }
		// `ClrRef<T>` is an intrinsic managed-reference marker (erased on the argument path) -> never emitted as a class.
		// @ClrTypeAlias classes (collections/StringBuilder/unsigned/primitives/String/…) are emitted here as ORDINARY
		// types; bir2cir's AliasHelperHoist drops them (and hoists a class's rule-3 members). kotc no longer strips them.
		// dll2klib-projected external .NET types (a `import P.Calc`/`P.SpanOps` host type, an inherited/implemented .NET
		// base) enter FIR through a reference KLIB with a library origin. They are REFERENCED types, never ours to
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
			// consuming module restores it as `val`, rejecting external writes (#34b, mirrors
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
		// A top-level property's get_/set_<name> as STATIC methods (the receiver, if any, rides `__self`) — emitted
		// only when the accessor is CUSTOM (not the trivial `field` passthrough). Covers a NO-backing-field property
		// (an EXTENSION property `val T.p`, or a computed `val p get() = …`) AND a backing-field property that ALSO
		// carries a custom accessor (`val p = 41; get() = field + 1`, #89) — its custom accessor must be emitted so
		// the read routes through it instead of a raw static-field load. A DEFAULT accessor emits none: the field
		// (above) is read/written directly. Getter and setter are decided independently (a `var` may pair a default
		// getter with a custom setter).
		val topPropAccessors = topProps.flatMap { p ->
			listOfNotNull(
				p.getter?.takeIf { fieldRoutedProperty(p) && !hasDefaultGetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
				p.setter?.takeIf { fieldRoutedProperty(p) && !hasDefaultSetter(p) }?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
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




	/**
	 * A lambda -> a delegate. Non-capturing lambdas lift to a static method (`newDelegate`); capturing
	 * lambdas synthesize a closure class (fields = captured vars, instance `invoke` method) (`newClosure`).
	 */


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


	// kotc emits ONLY the faithful op + faithful operand expression nodes — no cast-stripped static-TYPE HINTS
	// (stripImplicit/stripCast); bir2cir (StaticType / StaticTypeResolver.cs) recovers each operand's static
	// type STRUCTURALLY off the emitted node + a local/param type environment — the single uniform source for the
	// collection/float/toString/nullable Kotlin-semantic recognition (those helpers live in bir2cir).

}
