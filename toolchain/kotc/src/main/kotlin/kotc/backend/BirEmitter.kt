package kotc.backend

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
	internal fun unsupported(node: IrElement?, what: String, detail: String): String {
		// Compiling the stdlib ITSELF (DOTKT_STDLIB_COMPILE): don't fail the whole file on one unsupported construct in
		// one op's body — emit a THROWING stub (a `throw NotSupportedException("[DOTKT-STDLIB] …")`) and warn. The op
		// is left a compiler lowering (NOT migrated off COLLECTION_OPS), so the stub is never actually called; this lets
		// the supported ops in the same file compile while the few backend-gap ops (object-expr-captures-T, …) wait.
		if (System.getenv("DOTKT_STDLIB_COMPILE") != null) {
			messageCollector?.report(CompilerMessageSeverity.WARNING,
				"[DOTKT-STDLIB] stubbed (not migrated, keep its lowering): $what — $detail", locationOf(node))
			return """{"k":"throwExpr","value":{"k":"clrNew","type":"System.NotSupportedException","argTypes":["System.String"],"args":[{"k":"const","type":"string","value":${str("[DOTKT-STDLIB] not lowered: $what")}}]}}"""
		}
		hadError = true
		messageCollector?.report(CompilerMessageSeverity.ERROR,
			"the .NET backend does not support $what yet: $detail", locationOf(node))
		return """{"k":"unsupportedExpr","of":${str("$what — $detail")}}"""
	}

	// Inline substitutions for synthetic temporaries (e.g. a `when` subject) in expression position.
	internal val valSubst = HashMap<String, String>()
	// While splicing an `inline fun` body: its type param name -> the call's substituted type-argument BIR (see birType).
	internal val typeArgSubst = HashMap<String, String>()

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
	internal data class TypeArgScope(val keys: List<String>, val old: Map<String, String?>, val had: Set<String>)
	internal val inlineLambdaTypeScopes = java.util.IdentityHashMap<IrFunctionExpression, TypeArgScope>()
	internal var inlCounter = 0
	internal var scopeCounter = 0
	internal var fileClass = ""   // current file's static class name (for top-level property access)
	// Per-file prefix for SYNTHETIC type names (closures, ref cells, sequence SMs). Each file is compiled by its own
	// BirEmitter with a fresh `closureCounter`, so unprefixed names like `<>dotkt_Closure0` COLLIDE across files when
	// ilemit links all BIR into one assembly (the dup overwrites in `_types` -> orphaned TypeBuilder -> Save crash).
	// `fileClass` is unique per file, so it disambiguates. Stays under the `<>dotkt_` prefix (ilemit marks those).
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
		val base = File(f.fileEntry.name).name.removeSuffix(".kt").replaceFirstChar { it.uppercaseChar() } + "Kt"
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
	// A local delegated property's getter/setter function -> the IrLocalDelegatedProperty, so call() rewrites a
	// `<get-x>`/`<set-x>` call to access on the delegate local (mirrors the member-property delegate path).
	internal val localDelegates = java.util.IdentityHashMap<IrSimpleFunction, IrLocalDelegatedProperty>()
	// The `buf` parameter of an active `stackBuffer { buf -> … }` block -> its stack allocation (ptr local + length
	// local + element type), so `buf[i]`/`buf[i]=v`/`buf.size` rewrite to stack ops while the block is spliced.
	internal class StackBufInfo(val ptrName: String, val lenName: String, val elemT: String)
	internal val stackBufSubst = java.util.IdentityHashMap<IrValueDeclaration, StackBufInfo>()
	// Synthetic monomorphized interfaces for the Kotlin iterator protocol. IL can't define a generic
	// interface yet, so per concrete element type we emit a non-generic `KIterator_<elem>` with
	// `hasNext():bool` / `next():<elem>` (Codex-advised monomorphization). elemBir -> interface name.
	internal val iterIfaces = LinkedHashMap<String, String>()
	// A custom (non-lazy) delegated property passes a `KProperty<*>` to getValue/setValue. KProperty has no
	// BCL equivalent (pure binding), so — like Kotlin/JVM's PropertyReferenceImpl — we compiler-generate a
	// minimal `KProperty` interface (`name`) + `KPropertyImpl(name)` class into the user's assembly.
	internal var needsKProperty = false

	/** A user/anon class's emitted name (anon "<no name provided>" -> its synthetic lifted name). */
	// A user type's .NET name = its Kotlin package projected as the .NET namespace (`alpha.Box`), so classes with the
	// same simple name in different packages don't collide in the assembly (they did — they all flattened to the root
	// namespace). NESTED types stay simple-named (their outer carries the namespace); anon/synthetic names are already
	// unique. Root-package types are unchanged (fqName has no dot), so existing code is unaffected. birType references
	// user types through here, so the def name and every reference stay consistent.
	internal fun typeName(k: IrClass): String =
		anonNames[k] ?: if (k.isCompanion && k.parent is IrClass) typeName(k.parent as IrClass)
		else if (k.parent is IrClass) {
			val p = k.parent as IrClass
			val owner = if (p.isCompanion) p.parent as? IrClass else p
			(owner?.let { typeName(it) + "." } ?: "") + k.name.asString()
		}
		else (k.fqNameWhenAvailable?.asString() ?: k.name.asString())

	internal fun emittedNestedParent(k: IrClass): IrClass? {
		val p = k.parent as? IrClass ?: return null
		return if (p.isCompanion) p.parent as? IrClass else p
	}

	// Synthesized stdlib delegate classes for Delegates.observable/vetoable/notNull (their stdlib bodies are
	// absent from our IR, so we compiler-generate equivalents, monomorphized by value type, each implementing
	// the synthetic RWProperty_<V>). Keyed "<kind>:<V>" -> class name; defs accumulated for emission.
	internal val synthDelegates = LinkedHashMap<String, String>()
	internal val synthDelegateDefs = ArrayList<String>()

	/** Register (once) a synthesized observable/vetoable/notNull delegate class for value type V; return its name. */
	internal fun synthDelegate(kind: String, v: String): String = synthDelegates.getOrPut("$kind:$v") {
		needsKProperty = true
		val safe = v.replace(Regex("[^A-Za-z0-9]"), "_")
		val cname = "<>dotkt_${kind}Delegate_$safe"
		val iface = propIface0("kotlin.properties.ReadWriteProperty", v)   // RWProperty_<V>; registers it
		val thisRef = """{"name":"thisRef","type":"object"}"""
		val kp = """{"name":"property","type":"@<>dotkt_KProperty"}"""
		val fieldVal = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":"value"}"""
		val setVal = { e: String -> """{"k":"setField","ownerType":${str(cname)},"recv":{"k":"this"},"name":"value","value":$e}""" }
		val getter = """{"name":"getValue","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[$thisRef,$kp],"ret":${str(v)},"body":[{"k":"return","value":$fieldVal}]}"""
		val (fields, ctorParams, ctorBody, setter) = when (kind) {
			"observable", "vetoable" -> {
				// KProperty erased to object in the callback type (see birTypeDeleg) -> matches the passed lambda.
				val fnT = if (kind == "observable") "func:void:object,$v,$v" else "func:bool:object,$v,$v"
				val onChange = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":"onChange"}"""
				val invoke = """{"k":"delegateInvoke","funcType":${str(fnT)},"recv":$onChange,"args":[{"k":"local","name":"property"},{"k":"local","name":"__old"},{"k":"local","name":"newValue"}]}"""
				val flds = """{"name":"value","type":${str(v)}},{"name":"onChange","type":${str(fnT)}}"""
				val cps = """{"name":"value","type":${str(v)}},{"name":"onChange","type":${str(fnT)}}"""
				val cb = """${setVal("""{"k":"local","name":"value"}""")},{"k":"setField","ownerType":${str(cname)},"recv":{"k":"this"},"name":"onChange","value":{"k":"local","name":"onChange"}}"""
				val old = """{"k":"var","name":"__old","type":${str(v)},"init":$fieldVal}"""
				val body = if (kind == "observable")
					"""$old,${setVal("""{"k":"local","name":"newValue"}""")},{"k":"exprStmt","expr":$invoke}"""
				else // vetoable: only store if the callback approves
					"""$old,{"k":"if","branches":[{"cond":$invoke,"body":[${setVal("""{"k":"local","name":"newValue"}""")}]}]}"""
				val st = """{"name":"setValue","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[$thisRef,$kp,{"name":"newValue","type":${str(v)}}],"ret":"void","body":[$body]}"""
				listOf(flds, cps, cb, st)
			}
			else -> { // notNull: throws until first set (lateinit-style); flag tracks whether assigned
				val flag = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":"__set"}"""
				val flds = """{"name":"value","type":${str(v)}},{"name":"__set","type":"bool"}"""
				val getBody = """{"k":"if","branches":[{"cond":{"k":"un","op":"!","e":$flag},"body":[{"k":"exprStmt","expr":{"k":"throwExpr","value":{"k":"clrNew","type":"System.InvalidOperationException","argTypes":["System.String"],"args":[{"k":"const","type":"string","value":"Property has not been initialized"}]}}}]}]},{"k":"return","value":$fieldVal}"""
				val st = """{"name":"setValue","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[$thisRef,$kp,{"name":"newValue","type":${str(v)}}],"ret":"void","body":[${setVal("""{"k":"local","name":"newValue"}""")},{"k":"setField","ownerType":${str(cname)},"recv":{"k":"this"},"name":"__set","value":{"k":"const","type":"bool","value":true}}]}"""
				// override getter body for notNull (throws if unset)
				return@getOrPut cname.also {
					synthDelegateDefs.add("""{"name":${str(cname)},"kind":"class","vis":"public","base":null,"interfaces":[${str(iface)}],"fields":[$flds],"ctors":[{"params":[],"baseArgs":null,"thisArgs":null,"vis":"public","body":[]}],"methods":[{"name":"getValue","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[$thisRef,$kp],"ret":${str(v)},"body":[$getBody]},$st]}""")
				}
			}
		}
		synthDelegateDefs.add("""{"name":${str(cname)},"kind":"class","vis":"public","base":null,"interfaces":[${str(iface)}],"fields":[$fields],"ctors":[{"params":[$ctorParams],"baseArgs":null,"thisArgs":null,"vis":"public","body":[$ctorBody]}],"methods":[$getter,$setter]}""")
		cname
	}

	/** The compiler-generated `KProperty` interface + `KPropertyImpl` class, if any delegated property used one. */
	internal fun kPropertyDefs(): List<String> {
		if (!needsKProperty) return emptyList()
		val ifaceName = """{"name":"get_name","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":"string","body":[]}"""
		val iface = """{"name":"<>dotkt_KProperty","kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$ifaceName]}"""
		val getName = """{"name":"get_name","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":"string","body":[{"k":"return","value":{"k":"field","ownerType":"<>dotkt_KPropertyImpl","recv":{"k":"this"},"name":"name"}}]}"""
		val ctorBody = """{"k":"setField","ownerType":"<>dotkt_KPropertyImpl","recv":{"k":"this"},"name":"name","value":{"k":"local","name":"name"}}"""
		val impl = """{"name":"<>dotkt_KPropertyImpl","kind":"class","vis":"public","base":null,"interfaces":["<>dotkt_KProperty"],"fields":[{"name":"name","type":"string"}],"ctors":[{"params":[{"name":"name","type":"string"}],"baseArgs":null,"thisArgs":null,"vis":"public","body":[$ctorBody]}],"methods":[$getName]}"""
		return listOf(iface, impl)
	}

	internal fun kIteratorName(elemBir: String): String =
		iterIfaces.getOrPut(elemBir) { "<>dotkt_KIterator_" + elemBir.replace(Regex("[^A-Za-z0-9]"), "_") }

	/** `kotlin.collections.(Mutable)Iterator<E>` -> the monomorphized synthetic interface name, else null. */
	internal fun iteratorElemIface(t: IrType): String? {
		val fq = t.classFqName?.asString() ?: return null
		if (fq != "kotlin.collections.Iterator" && fq != "kotlin.collections.MutableIterator") return null
		val elem = (t as? IrSimpleType)?.arguments?.firstOrNull()
			?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
		// An element that CONTAINS a type param (`gp:E`, or `System.ValueTuple[int,gp:T]` from `IndexedValue<T>`) can't be
		// a monomorphized synthetic — it would bake the unresolvable `gp:*`. Don't register/emit one; birType maps it to
		// the CLR-native generic IEnumerator instead (keystone fix for generic `class HashSet<E>` / `withIndex()` etc).
		if (elem.contains("gp:")) return null
		return kIteratorName(elem)
	}

	// `kotlin.collections.(Mutable)Iterable<E>` -> a monomorphized synthetic interface `<>dotkt_KIterable_<elem>`
	// with `operator fun iterator(): KIterator_<elem>` (same IL-can't-define-generic-interface workaround as
	// Iterator). Lets a user `class R : Iterable<T>` link a real supertype and a `for (x in r)` resolve its iterator.
	internal val iterableIfaces = LinkedHashMap<String, String>()
	internal fun kIterableName(elemBir: String): String =
		iterableIfaces.getOrPut(elemBir) { kIteratorName(elemBir); "<>dotkt_KIterable_" + elemBir.replace(Regex("[^A-Za-z0-9]"), "_") }
	internal fun iterableElemIface(t: IrType): String? {
		val fq = t.classFqName?.asString() ?: return null
		if (fq != "kotlin.collections.Iterable" && fq != "kotlin.collections.MutableIterable") return null
		val elem = (t as? IrSimpleType)?.arguments?.firstOrNull()
			?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
		if (elem.contains("gp:")) return null   // element contains a type param -> CLR-native IEnumerable, no synthetic
		return kIterableName(elem)
	}

	// kotlin.CharSequence has no faithful .NET equivalent (it's a read-only INDEXED polymorphic char view — neither
	// IEnumerable<char>, char[], nor IReadOnlyList<char> fits, and String doesn't implement any of them as a common
	// supertype). So a user `class S : CharSequence` gets a synthetic monomorphized interface `<>dotkt_CharSequence`
	// (length getter + get(i) operator + subSequence). To pass a .NET string API, call `.toString()`.
	internal var usesCharSeq = false
	internal fun charSeqIface(t: IrType): String? =
		if (t.classFqName?.asString() == "kotlin.CharSequence") { usesCharSeq = true; "<>dotkt_CharSequence" } else null
	internal fun charSeqIfaceDefs(): List<String> = if (!usesCharSeq) emptyList() else listOf({
		val getLen = """{"name":"get_length","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":"int","body":[]}"""
		val get = """{"name":"get","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"index","type":"int"}],"ret":"char","body":[]}"""
		val subSeq = """{"name":"subSequence","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[{"name":"startIndex","type":"int"},{"name":"endIndex","type":"int"}],"ret":"@<>dotkt_CharSequence","body":[]}"""
		"""{"name":"<>dotkt_CharSequence","kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$getLen,$get,$subSeq]}"""
	}())

	// kotlin.properties.Read(Write)Property<T,V> -> monomorphized-by-V synthetic interfaces (like the iterator
	// protocol). The user delegate class implements one of these; getValue/setValue take (thisRef, KProperty[, V]).
	internal val roPropIfaces = LinkedHashMap<String, String>()   // V (birType) -> interface name
	internal val rwPropIfaces = LinkedHashMap<String, String>()

	/** `kotlin.properties.Read(Write)Property<T,V>` -> the monomorphized synthetic interface name, else null. */
	internal fun propIface(t: IrType): String? {
		val fq = t.classFqName?.asString() ?: return null
		if (fq != "kotlin.properties.ReadWriteProperty" && fq != "kotlin.properties.ReadOnlyProperty") return null
		val v = (t as? IrSimpleType)?.arguments?.getOrNull(1)?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
		// A value type that CONTAINS a type param can't be a monomorphized synthetic (it would bake an unresolvable
		// `gp:*`); fall through to the real generic stdlib ReadWriteProperty/ReadOnlyProperty interface instead. (Step
		// toward retiring the kotlin.* synthetics now that the stdlib defines these interfaces.)
		if (v.contains("gp:")) return null
		return propIface0(fq, v)
	}

	/** Register (once) the synthetic Read(Write)Property interface for value type `v`; return its name. */
	internal fun propIface0(fq: String, v: String): String {
		needsKProperty = true
		val safe = v.replace(Regex("[^A-Za-z0-9]"), "_")
		return if (fq == "kotlin.properties.ReadWriteProperty") rwPropIfaces.getOrPut(v) { "<>dotkt_RWProperty_$safe" }
		else roPropIfaces.getOrPut(v) { "<>dotkt_ROProperty_$safe" }
	}

	/** BIR defs for every synthesized Read(Write)Property interface (getValue/setValue over (thisRef, KProperty)). */
	internal fun propIfaceDefs(): List<String> {
		fun m(name: String, params: String, ret: String) =
			"""{"name":${str(name)},"static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[$params],"ret":${str(ret)},"body":[]}"""
		val kp = """{"name":"property","type":"@<>dotkt_KProperty"}"""
		val thisRef = """{"name":"thisRef","type":"object"}"""
		val out = ArrayList<String>()
		roPropIfaces.forEach { (v, name) ->
			val getV = m("getValue", "$thisRef,$kp", v)
			out.add("""{"name":${str(name)},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$getV]}""")
		}
		rwPropIfaces.forEach { (v, name) ->
			val getV = m("getValue", "$thisRef,$kp", v)
			val setV = m("setValue", "$thisRef,$kp,{\"name\":\"value\",\"type\":${str(v)}}", "void")
			out.add("""{"name":${str(name)},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$getV,$setV]}""")
		}
		return out
	}

	/** BIR defs for every synthesized Kotlin-iterator interface seen while emitting this file. */
	internal fun iteratorIfaceDefs(): List<String> = iterIfaces.entries.map { (elem, name) ->
		val hasNext = """{"name":"hasNext","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":"bool","body":[]}"""
		val next = """{"name":"next","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":${str(elem)},"body":[]}"""
		"""{"name":${str(name)},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$hasNext,$next]}"""
	} + iterableIfaces.entries.map { (elem, name) ->
		// `KIterable_<elem>` -> `iterator(): KIterator_<elem>` (kIterableName already registered the KIterator).
		val iter = """{"name":"iterator","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":${str("@" + kIteratorName(elem))},"body":[]}"""
		"""{"name":${str(name)},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$iter]}"""
	}

	// `kotlin.Result<T>` -> the shared `DotKt.Result<T>` struct (T4): runCatching builds it via
	// Success/Failure; accessors inline in call()/expr() over its IsSuccess/Value/ExceptionOrNull properties. No
	// per-assembly synthesis (the earlier `<>dotkt_Result` is retired — one cross-assembly type, see docs §13n).

	// heap ref-cell: local `var`s captured-and-mutated by a (non-inline) closure / object / local class are promoted
	// to a shared `<>dotkt_Ref<T>{ var v }` so the mutation is visible across the capture boundary. Per top-level
	// function (set in `method`/`ctor`); all reads/writes of such a var go through `.v`.
	internal var refCellVars: Set<IrValueDeclaration> = emptySet()
	internal val refTypes = LinkedHashMap<String, String>()   // element birType -> monomorphized Ref class name
	internal fun refTypeName(d: IrValueDeclaration): String {
		val elem = birType(d.type)
		return refTypes.getOrPut(elem) { "<>dotkt_${synthScope}_Ref_" + elem.replace(Regex("[^A-Za-z0-9]"), "_") }
	}
	internal fun refDefs(): List<String> = refTypes.map { (elem, name) ->
		// A monomorphized heap cell `class <>dotkt_Ref_<elem>(var v: elem)` (non-generic -> trivial field access).
		val ctor = """{"params":[{"name":"v","type":${str(elem)}}],"baseArgs":null,"thisArgs":null,"vis":"public","body":[{"k":"setField","ownerType":${str(name)},"recv":{"k":"this"},"name":"v","value":{"k":"local","name":"v"}}]}"""
		"""{"name":${str(name)},"kind":"class","abstract":false,"vis":"public","typeParams":[],"base":null,"interfaces":[],"fields":[{"name":"v","type":${str(elem)}}],"ctors":[$ctor],"methods":[]}"""
	}
	internal fun isRefCell(d: IrValueDeclaration) = d in refCellVars
	/** The Ref-typed base expression for a ref-cell var: its capture field inside a closure, else the local. */
	internal fun refBase(d: IrValueDeclaration) = captureSubst[d] ?: """{"k":"local","name":${str(d.name.asString())}}"""
	/** A captured value's type as held in the closure: the Ref cell for a ref-cell var, else its plain type. */
	internal fun captureFieldType(d: IrValueDeclaration) = if (isRefCell(d)) "@" + refTypeName(d) else birType(d.type)

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
	 *  so a suspension in the lambda body is the ENCLOSING coroutine's — the CPS path linearizes them (emitScopeCps). */
	internal fun scopeCall(e: org.jetbrains.kotlin.ir.IrElement?): Triple<String, IrExpression, IrFunctionExpression>? {
		val call = e as? IrCall ?: return null
		val fq = call.symbol.owner.fqNameWhenAvailable?.asString() ?: return null
		if (fq !in SCOPE_FUNCTIONS) return null
		val isWith = fq == "kotlin.with"
		val recv = (if (isWith) regularArgs(call).getOrNull(0) else extensionReceiver(call)) ?: return null
		val lambda = (if (isWith) regularArgs(call).getOrNull(1) else regularArgs(call).getOrNull(0)) as? IrFunctionExpression ?: return null
		return Triple(fq, recv, lambda)
	}

	/** A scope-function call whose lambda body DIRECTLY contains a suspension (so it must be CPS-inlined, not rendered
	 *  as a value-block). The receiver expression is checked too (`with(suspendExpr()){…}`). */
	internal fun scopeSuspendCall(e: org.jetbrains.kotlin.ir.IrElement?): Triple<String, IrExpression, IrFunctionExpression>? =
		scopeCall(e)?.takeIf { (_, recv, lambda) -> lambda.function.body?.let { containsSuspend(it) } == true || containsSuspend(recv) }

	fun emitFile(file: IrFile): String {
		fileEntry = file.fileEntry
		// Per-FILE lifted state. One BirEmitter instance processes every file in turn, so these MUST be reset here —
		// otherwise each file's BIR accumulates the previous files' lifted lambdas/types, duplicating them into every
		// file class (e.g. App.kt's `__lambda*` reappearing in ControlsKt/DslKt/…). The `<>dotkt_*` types are
		// de-duplicated by ilemit, but lifted `__lambdaN` are file-class methods that are NOT — so the duplication is
		// real metadata bloat and a correctness hazard.
		liftedMethods.clear(); liftedTypes.clear(); synthDelegateDefs.clear(); refTypes.clear()
		iterIfaces.clear(); iterableIfaces.clear(); roPropIfaces.clear(); rwPropIfaces.clear()
		usesCharSeq = false; needsKProperty = false
		// The `@ClrAwait` await intrinsic (`fun <T> Task<T>.await(): T`) is never emitted as a real method —
		// its call sites are lowered to coroutine suspension points (see suspendMethod). Skip it.
		// The `byref` out/ref marker is an intrinsic consumed at its call sites (the arg becomes a `byref:` param) —
		// never emitted as a real method.
		// Only USER functions (origin DEFINED) — a consuming module's FIR also holds plugin-INJECTED top-level funs
		// (stdlib ops restored from a referenced DotKt.Stdlib, in the synthetic `__GENERATED DECLARATIONS__` file);
		// those are the library's to provide, not ours to re-emit (a re-emitted stub has no real body -> invalid IL).
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && !isAwaitIntrinsic(it) && it.name.asString() !in setOf("byref", "stackBuffer") }
			.filterNot { skipStdlibHighArityFunctionType(it) }
		// `ClrRef<T>` is an intrinsic managed-reference marker (erased on the argument path) -> never emitted as a class.
		val classes = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.CLASS && clrName(it) == null && it.name.asString() !in setOf("ClrRef", "StackBuffer", "Span") }
		// `object Foo { ... }` (non-companion) -> a singleton class with a static `INSTANCE` field; `IrGetObjectValue`
		// loads it. The shared-state-via-`object` case (feedback item 10). Companion/anonymous objects are handled
		// elsewhere; .NET-injected `object`s (Math, …) are static call sites, not user singletons.
		val objects = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.OBJECT && !it.isCompanion && clrName(it) == null }
		val interfaces = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.INTERFACE && clrName(it) == null }
		val enums = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.ENUM_CLASS }
		val annClasses = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.ANNOTATION_CLASS && clrName(it) == null }
		// Only USER properties (origin DEFINED) — a consuming module's FIR also holds plugin-INJECTED top-level props
		// (restored extension properties from a referenced DotKt assembly); those are the library's, not ours to emit.
		val topProps = file.declarations.filterIsInstance<IrProperty>().filter { it.origin.toString() == "DEFINED" }
		if (functions.isEmpty() && classes.isEmpty() && objects.isEmpty() && interfaces.isEmpty() && enums.isEmpty() && annClasses.isEmpty() && topProps.isEmpty()) return ""
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
			"""{"name":${str(bf.name.asString())},"type":${str(birType(bf.type))},"static":true,"init":$init}"""
		}
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
			(richEnums + nestedRichEnums).map { richEnumDef(it) } + annClasses.map { annotationDef(it) }
		val methods = (fnMethods + topPropAccessors + liftedMethods).joinToString(",")
		// Synthetic types (iterator/Read(Write)Property interfaces, synthesized Delegates.* classes, KProperty)
		// are registered lazily while emitting bodies above -> append last (order matters: producers before
		// kPropertyDefs/propIfaceDefs, which read flags/maps the producers populate).
		val synthDelegateTypes = synthDelegateDefs.joinToString(",").let { if (it.isEmpty()) emptyList() else listOf(it) }
		val types = (typeDefs + liftedTypes + synthDelegateTypes + iteratorIfaceDefs() + charSeqIfaceDefs() + propIfaceDefs() + kPropertyDefs() + refDefs()).joinToString(",")
		return """{"fileClass":${str(className)},"hasMain":$hasMain,"fields":[${statFields.joinToString(",")}],"methods":[$methods],"types":[$types]}"""
	}

	internal fun interfaceDef(iface: IrClass): String {
		fun ifaceMethod(it: IrSimpleFunction, prop: IrProperty? = it.correspondingPropertySymbol?.owner): String {
			val name = prop?.let { p -> (if (it == p.getter) "get_" else "set_") + p.name.asString() } ?: it.name.asString()
			val ret = if (prop != null && it == prop.setter) "void" else birType(it.returnType)
			// A generic interface method (`fun <E> get(...)`, `<R> fold(...)`) must carry its own type params, else
			// `gp:E`/`gp:R` in its signature is unresolvable at emit (CoroutineContext / ContinuationInterceptor / …).
			return """{"name":${str(name)},"static":false,"override":false,"virtual":true${typeParamsJson(it.typeParameters)},"params":[${paramsJson(it.parameters)}],"ret":${str(ret)},"body":[]}"""
		}
		val funMethods = iface.declarations.filterIsInstance<IrSimpleFunction>()
			.filterNot { it.signatureMentionsJava() }
			.filterNot { skipStdlibHighArityFunctionType(it) }
			.map { ifaceMethod(it) }
		val propMethods = iface.declarations.filterIsInstance<IrProperty>()
			.flatMap { p -> listOfNotNull(p.getter?.let { ifaceMethod(it, p) }, p.setter?.let { ifaceMethod(it, p) }) }
		val methods = (funMethods + propMethods).distinct().joinToString(",")
		// A nested interface (`TimeSource.WithComparableMarks`) -> a real CLR nested type of its enclosing class/interface.
		val nestedIn = emittedNestedParent(iface)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null }
			?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
		val ifaces = iface.superTypes
			.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
			.mapNotNull { st ->
				val bt = birType(st)
				if (bt.startsWith("func:")) null
				else if (bt.startsWith("clr:") || bt.startsWith("clrg:")) bt
				else (st.classifierOrNull?.owner as? IrClass)?.let { ownerSpec(it, st) }
			}
			.joinToString(",") { str(it) }
		return """{"name":${str(typeName(iface))},"kind":"interface"$nestedIn${typeParamsJson(iface.typeParameters)},"base":null,"interfaces":[$ifaces],"fields":[],"ctors":[],"methods":[$methods]}"""
	}

	internal fun IrSimpleFunction.signatureMentionsJava(): Boolean =
		typeMentionsJava(returnType) || parameters.any { it.kind == IrParameterKind.Regular && typeMentionsJava(it.type) }

	internal fun typeMentionsJava(t: IrType): Boolean {
		val fq = t.classFqName?.asString()
		if (fq != null && fq.startsWith("java.")) return true
		return (t as? IrSimpleType)?.arguments.orEmpty().any { (it as? IrTypeProjection)?.type?.let(::typeMentionsJava) == true }
	}

	internal fun skipStdlibHighArityFunctionType(fn: IrSimpleFunction): Boolean {
		if (System.getenv("DOTKT_STDLIB_COMPILE") == null) return false
		val arity = highArityFunctionParameterCount(fn.returnType)
			?: fn.parameters.firstNotNullOfOrNull { highArityFunctionParameterCount(it.type) }
			?: return false
		messageCollector?.report(CompilerMessageSeverity.WARNING,
			"[DOTKT-STDLIB] skipped ${fn.name.asString()}: function type with $arity parameters exceeds System.Func/Action's 16-parameter limit", locationOf(fn))
		return true
	}

	internal fun highArityFunctionParameterCount(t: IrType): Int? {
		val fqn = t.classFqName?.asString()
		val args = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
		if (fqn != null && (fqn.startsWith("kotlin.Function") || fqn.startsWith("kotlin.reflect.KFunction") ||
				fqn.startsWith("kotlin.coroutines.SuspendFunction")) && args.size > 1) {
			val parameterCount = args.size - 1
			if (parameterCount > 16) return parameterCount
		}
		return args.firstNotNullOfOrNull { highArityFunctionParameterCount(it) }
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
		val userFields = ec.declarations.filterIsInstance<IrProperty>().mapNotNull { it.backingField }
			.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		val setThis = { f: String, v: String -> """{"k":"setField","ownerType":${str(name)},"recv":{"k":"this"},"name":${str(f)},"value":$v}""" }
		val loc = { n: String -> """{"k":"local","name":${str(n)}}""" }
		// ctor(__name, __ordinal, <user params>) storing each into a field.
		val ctorParams = (listOf("""{"name":"__name","type":"string"}""", """{"name":"__ordinal","type":"int"}""") +
			userParams.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }).joinToString(",")
		val ctorBody = (listOf(setThis("__name", loc("__name")), setThis("__ordinal", loc("__ordinal"))) +
			userParams.map { setThis(it.name.asString(), loc(it.name.asString())) }).joinToString(",")
		// Per-entry bodies (`PLUS { override fun apply(…)=… }`): the base enum is abstract with abstract members, and
		// each such entry is its own subclass overriding them. Detect them + the abstract members. (T A-109.)
		val hasPerEntry = entries.any { it.correspondingClass != null }
		val absMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.correspondingPropertySymbol == null && it.body == null && it.modality == Modality.ABSTRACT }
			.filterNot { skipStdlibHighArityFunctionType(it) }
		val baseAbstract = hasPerEntry || absMethods.isNotEmpty()
		// base ctor must be callable from the entry subclasses -> protected (was private for the flat form).
		val ctor = """{"params":[$ctorParams],"baseArgs":null,"thisArgs":null,"vis":${str(if (hasPerEntry) "protected" else "private")},"body":[$ctorBody]}"""
		// instance fields: metadata + user props.
		val fields = (listOf("""{"name":"__name","type":"string"}""", """{"name":"__ordinal","type":"int"}""") + userFields).toMutableList()
		// per-entry static singleton, init = new <Enum-or-entry-subclass>("NAME", ordinal, <entry ctor args>).
		val subDefs = ArrayList<String>()
		val nameOrd = { i: Int, ent: IrEnumEntry -> listOf("""{"k":"const","type":"string","value":${str(ent.name.asString())}}""", """{"k":"const","type":"int","value":$i}""") }
		entries.forEachIndexed { i, ent ->
			val cc = ent.correspondingClass
			if (cc != null) {
				// A body entry `NAME(args) { override … }` is its own subclass `<>Enum_NAME : Enum`. The enum-super
				// args (the `args`) are baked into the subclass's base() call; the entry field constructs it with
				// just (__name, __ordinal) so the subclass ctor is uniform regardless of user params.
				val sub = "<>${name}_${ent.name.asString()}"
				subDefs.add(enumEntrySubclass(sub, name, cc, enumSuperArgs(cc)))
				fields.add("""{"name":${str(ent.name.asString())},"type":${str("@$name")},"static":true,"init":{"k":"new","type":${str(sub)},"args":[${nameOrd(i, ent).joinToString(",")}]}}""")
			} else {
				val ecc = (ent.initializerExpression as? IrExpressionBody)?.expression as? IrEnumConstructorCall
				val entryArgs = ecc?.let { regularArgs(it).map { a -> expr(a) } }.orEmpty()
				val newArgs = (nameOrd(i, ent) + entryArgs).joinToString(",")
				fields.add("""{"name":${str(ent.name.asString())},"type":${str("@$name")},"static":true,"init":{"k":"new","type":${str(name)},"args":[$newArgs]}}""")
			}
		}
		// methods: concrete user methods + abstract member decls + ToString + values() + valueOf().
		val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
			.filterNot { skipStdlibHighArityFunctionType(it) }
			.map { method(it, static = false) } +
			absMethods.map { m -> """{"name":${str(m.name.asString())},"static":false,"override":false,"virtual":true,"abstract":true,"vis":"public","params":[${paramsJsonList(m.parameters).joinToString(",")}],"ret":${str(birType(m.returnType))},"body":[]}""" }
		val sf = { e: IrEnumEntry -> """{"k":"staticField","ownerType":${str(name)},"name":${str(e.name.asString())}}""" }
		val toStr = """{"name":"ToString","static":false,"override":true,"virtual":true,"objectOverride":true,"vis":"public","params":[],"ret":"string","body":[{"k":"return","value":{"k":"field","ownerType":${str(name)},"recv":{"k":"this"},"name":"__name"}}]}"""
		val valuesArr = """{"k":"newArray","elem":${str("@$name")},"elems":[${entries.joinToString(",") { sf(it) }}]}"""
		val valuesM = """{"name":"values","static":true,"override":false,"virtual":false,"vis":"public","params":[],"ret":${str("array:@$name")},"body":[{"k":"return","value":$valuesArr}]}"""
		val voBranches = entries.joinToString(",") { ent ->
			"""{"cond":{"k":"objEq","l":{"k":"local","name":"name"},"r":{"k":"const","type":"string","value":${str(ent.name.asString())}}},"body":[{"k":"return","value":${sf(ent)}}]}"""
		}
		val voThrow = throwExpr(newExc("System.ArgumentException", str("No enum constant $name")))
		val voBody = """{"k":"if","branches":[$voBranches,{"else":true,"body":[{"k":"exprStmt","expr":$voThrow}]}]}"""
		val valueOfM = """{"name":"valueOf","static":true,"override":false,"virtual":false,"vis":"public","params":[{"name":"name","type":"string"}],"ret":${str("@$name")},"body":[$voBody]}"""
		val methods = (userMethods + listOf(toStr, valuesM, valueOfM)).joinToString(",")
		val baseDef = """{"name":${str(name)},"kind":"class","abstract":$baseAbstract,"vis":${str(visOf(ec))},"base":null,"interfaces":[],"fields":[${fields.joinToString(",")}],"ctors":[$ctor],"methods":[$methods]}"""
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
			.filterNot { skipStdlibHighArityFunctionType(it) }
			.joinToString(",") { method(it, static = false) }
		val baseArgs = (listOf("""{"k":"local","name":"__name"}""", """{"k":"local","name":"__ordinal"}""") + userArgs).joinToString(",")
		val subCtor = """{"params":[{"name":"__name","type":"string"},{"name":"__ordinal","type":"int"}],"baseArgs":[$baseArgs],"thisArgs":null,"vis":"public","body":[]}"""
		return """{"name":${str(subName)},"kind":"class","abstract":false,"vis":"public","base":${str(baseName)},"interfaces":[],"fields":[],"ctors":[$subCtor],"methods":[$overrides]}"""
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
	internal fun innerClassDef(inner: IrClass): String {
		val outerThis = (inner.parent as? IrClass)?.thisReceiver
			?: return typeDef(inner)   // not actually inner-of-class; emit plainly
		captureSubst[outerThis] = """{"k":"field","ownerType":${str(typeName(inner))},"recv":{"k":"this"},"name":"__outer"}"""
		val def = typeDef(inner, listOf(outerThis to "__outer"))
		captureSubst.remove(outerThis)
		return def
	}

	/** A property accessor with a user-written body (`get() = …` / `set(v) { … }`), not the default field passthrough. */
	internal fun isCustomAccessor(acc: IrSimpleFunction?): Boolean =
		acc != null && acc.origin.toString() == "DEFINED" && acc.body != null && acc.overriddenSymbols.isEmpty()
	internal fun hasCustomAccessor(prop: IrProperty): Boolean = isCustomAccessor(prop.getter) || isCustomAccessor(prop.setter)

	/** Emit a custom property accessor as a `get_<prop>`/`set_<prop>` method (the `field` identifier -> the backing field). */
	/** True if [t]'s class transitively extends kotlin.Throwable / a .NET-mapped exception (so `.message`/`.cause` on
	 *  a user exception subclass route to System.Exception.Message/.InnerException). */
	internal fun isThrowableType(t: IrType?): Boolean {
		val start = t?.classifierOrNull?.owner as? IrClass ?: return false
		val seen = HashSet<IrClass>()
		fun walk(c: IrClass): Boolean {
			if (!seen.add(c)) return false
			val fq = c.fqNameWhenAvailable?.asString()
			if (fq == "kotlin.Throwable" || (fq != null && NET_EXCEPTIONS.containsKey(fq))) return true
			return c.superTypes.any { (it.classifierOrNull?.owner as? IrClass)?.let(::walk) == true }
		}
		return walk(start)
	}

	// Considers the function itself AND any member it overrides — so it maps both a user override of a .NET-mapped
	// iface member AND a direct call on an iface-typed value (e.g. `cs.length` where cs: CharSequence).
	internal fun clrIfaceMemberName(fn: IrSimpleFunction): String? =
		(sequenceOf(fn) + fn.overriddenSymbols.asSequence().map { it.owner }).firstNotNullOfOrNull { owner ->
			val ifaceFq = (owner.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			val mn = owner.name.asString()
			when (ifaceFq) {
				// Comparable/Comparator are emitted by stdlib itself; keep their Kotlin ABI names.
				"kotlin.AutoCloseable", "java.lang.AutoCloseable", "java.io.Closeable", "kotlin.io.Closeable" -> if (mn == "close") "Dispose" else null
				// CharSequence -> synthetic <>dotkt_CharSequence: the `length` property getter must be emitted (the
				// override has a non-empty overriddenSymbols so isCustomAccessor is false). get/subSequence keep names.
				"kotlin.CharSequence" -> if (owner.correspondingPropertySymbol?.owner?.name?.asString() == "length") "get_length" else null
				else -> null
			}
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
		val selfParam = extRecv?.let { """{"name":"__self","type":${str(birType(it.type))}}""" }
		val ps = (listOfNotNull(selfParam) + paramsJsonList(acc.parameters)).joinToString(",")
		val name = (if (isGetter) "get_" else "set_") + propName
		val ret = if (isGetter) birType(acc.returnType) else "void"
		return """{"name":${str(name)},"static":true,"override":false,"virtual":false,"abstract":false,"objectOverride":false,"vis":"public"${typeParamsJson(acc.typeParameters)},"params":[$ps],"ret":${str(ret)},"body":[$body]}"""
	}

	internal fun accessorMethod(acc: IrSimpleFunction, propName: String, isGetter: Boolean): String {
		val mname = clrIfaceMemberName(acc) ?: (if (isGetter) "get_" else "set_") + propName
		// A MEMBER extension property (`class C { val T.p get() }`) has BOTH a dispatch and an extension receiver -> the
		// extension receiver rides a leading `__self` param (mirrors a member extension function); body refs to it
		// resolve via selfSubst (by identity, so it isn't confused with the dispatch `<this>`).
		val extRecv = acc.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
		val selfParam = extRecv?.let { """{"name":"__self","type":${str(birType(it.type))}}""" }
		val ps = (listOfNotNull(selfParam) + acc.parameters.filter { it.kind == IrParameterKind.Regular }
			.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }).joinToString(",")
		val body = (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		if (extRecv != null) selfSubst.remove(extRecv)
		val ret = if (isGetter) birType(acc.returnType) else "void"
		val clrIface = clrIfaceMemberName(acc) != null
		val virtual = clrIface || acc.modality == Modality.OPEN || acc.modality == Modality.ABSTRACT || acc.overriddenSymbols.isNotEmpty()
		val vis = if (clrIface) "public" else visOf(acc)
		val isAbstract = acc.modality == Modality.ABSTRACT && acc.body == null
		return """{"name":${str(mname)},"static":false,"override":$clrIface,"virtual":$virtual,"abstract":$isAbstract,"objectOverride":false,"vis":${str(vis)},"params":[$ps],"ret":${str(ret)},"body":[$body]}"""
	}

	/** A user `annotation class Ann(val v: Int, …)` -> a `class Ann : System.Attribute` (ctor params -> public fields). */
	internal fun annotationDef(klass: IrClass): String {
		val ctorParams = klass.declarations.filterIsInstance<IrConstructor>().firstOrNull { it.isPrimary }
			?.parameters?.filter { it.kind == IrParameterKind.Regular }.orEmpty()
		val fields = ctorParams.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		val assigns = ctorParams.joinToString(",") { """{"k":"setField","ownerType":${str(typeName(klass))},"recv":{"k":"this"},"name":${str(it.name.asString())},"value":{"k":"local","name":${str(it.name.asString())}}}""" }
		val ctor = """{"params":[$fields],"baseArgs":[],"thisArgs":null,"vis":"public","body":[$assigns]}"""
		return """{"name":${str(typeName(klass))},"kind":"class","abstract":false,"vis":"public","base":"clr:System.Attribute","interfaces":[],"fields":[$fields],"ctors":[$ctor],"methods":[]}"""
	}

	/** The `attrs` JSON for a declaration: each annotation -> a .NET custom attribute application. A Kotlin-authored
	 *  annotation uses its synthesized `: System.Attribute` type (#46); an imported .NET attribute uses its real type
	 *  via a `clr:` marker so ilemit binds the existing .NET constructor (#54). Kotlin built-in annotations are dropped. */
	internal fun attrsJson(anns: List<IrConstructorCall>): String =
		anns.mapNotNull { ann ->
			val ac = ann.symbol.owner.parent as? IrClass ?: return@mapNotNull null
			if (ac.kind != ClassKind.ANNOTATION_CLASS) return@mapNotNull null
			val clr = clrName(ac)
			if (clr == null && ac.fqNameWhenAvailable?.asString()?.startsWith("kotlin.") == true) return@mapNotNull null
			val attrType = if (clr != null) "clr:$clr" else typeName(ac)
			val args = regularArgs(ann)
			"""{"attr":${str(attrType)},"argTypes":[${args.joinToString(",") { str(netType(it.type)) }}],"args":[${args.joinToString(",") { expr(it) }}]}"""
		}.joinToString(",")

	internal fun typeDef(klass: IrClass, captures: List<Pair<IrValueDeclaration, String>> = emptyList(), isObject: Boolean = false): String {
		val baseType = klass.superTypes
			.firstOrNull { val k = it.classifierOrNull?.owner as? IrClass; k != null && k.kind == ClassKind.CLASS && k.fqNameWhenAvailable?.asString() != "kotlin.Any" }
		val base = baseType?.classifierOrNull?.owner as? IrClass
		val companion = klass.declarations.filterIsInstance<IrClass>().firstOrNull { it.isCompanion }
		val instFields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { p ->
			val bf = p.backingField ?: return@mapNotNull null
			// Honor the property's visibility on its backing field (A-108): a `private`/`internal`/`protected`
			// property gets a non-public field. (Kotlin's own access rules already keep same-class field reads valid.)
			val v = visOf(p); val visJson = if (v != "public") ""","vis":${str(v)}""" else ""
			// A property that isn't publicly SETTABLE (`val`, or `var ... private/protected set`) -> mark the public
			// backing field read-only so a consuming Kotlin module restores it as `val` (rejecting external writes).
			val ro = if (!p.isVar || (p.setter != null && visOf(p.setter!!) != "public")) ""","readOnly":true""" else ""
			"""{"name":${str(bf.name.asString())},"type":${str(birType(bf.type))}$visJson$ro}"""
		}
		// Companion non-const `val`/`var` -> static fields (with initializer run in a static ctor); const is inlined.
		val statFields = companion?.declarations?.filterIsInstance<IrProperty>()?.mapNotNull { p ->
			val bf = p.backingField ?: return@mapNotNull null
			if (p.isConst) return@mapNotNull null
			val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
			"""{"name":${str(bf.name.asString())},"type":${str(birType(bf.type))},"static":true,"init":$init}"""
		}.orEmpty()
		// A capturing object literal carries its captured outer values as extra instance fields.
		val capFields = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		// `object` singleton: a static `INSTANCE` field initialized to `new Foo()` (run in the .cctor) — same shape
		// as an enum entry. `IrGetObjectValue` loads it; member access then routes as normal instance access.
		val instanceField = if (isObject)
			listOf("""{"name":"INSTANCE","type":${str("@" + typeName(klass))},"static":true,"init":{"k":"new","type":${str(typeName(klass))},"args":[]}}""")
		else emptyList()
		val fields = (instFields + statFields + capFields + instanceField).joinToString(",")
		val ctors = klass.declarations.filterIsInstance<IrConstructor>().joinToString(",") { ctor(klass, it, captures) }
		val instMethods = klass.declarations.filterIsInstance<IrSimpleFunction>()
			// Include `abstract fun`s (body == null): they emit as CLR abstract methods so subclass overrides bind
			// and a base-typed call (`shape.area()`) resolves to the slot.
			.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && (it.body != null || it.modality == Modality.ABSTRACT) }
			.filterNot { skipStdlibHighArityFunctionType(it) }
			.map { method(it, static = false) }
		// Companion methods -> static methods of the enclosing class.
		val statMethods = companion?.declarations?.filterIsInstance<IrSimpleFunction>()
			?.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride && it.body != null }
			?.filterNot { skipStdlibHighArityFunctionType(it) }
			?.map { method(it, static = true) }.orEmpty()
		val companionAccessors = companion?.declarations?.filterIsInstance<IrProperty>()
			?.filter { it.backingField == null }
			?.flatMap { p ->
				listOfNotNull(
					p.getter?.let { topLevelAccessorMethod(it, p.name.asString(), true) },
					p.setter?.let { topLevelAccessorMethod(it, p.name.asString(), false) })
			}.orEmpty()
		// Property accessors that override a .NET base virtual property -> emitted as get_/set_ override methods.
		val clrAccessors = klass.declarations.filterIsInstance<IrProperty>()
			.flatMap { p -> listOfNotNull(clrAccessorMethod(p, p.getter), clrAccessorMethod(p, p.setter)) }
		// User custom accessors (`get() = …`/`set(v){…}`) -> get_/set_ methods (the access site routes through them).
		val userAccessors = klass.declarations.filterIsInstance<IrProperty>().flatMap { p ->
			listOfNotNull(
				p.getter?.takeIf { hasCustomAccessor(p) || clrIfaceMemberName(it) != null || p.backingField == null }?.let { accessorMethod(it, p.name.asString(), true) },
				p.setter?.takeIf { hasCustomAccessor(p) || clrIfaceMemberName(it) != null || p.backingField == null }?.let { accessorMethod(it, p.name.asString(), false) })
		}
		val methods = (instMethods + statMethods + companionAccessors + clrAccessors + userAccessors).joinToString(",")
		// A .NET base class (`: System.Exception(...)`, incl. a generic `: Collection<Int>()`) -> a `clr:`/`clrg:`
		// type spec (via birType) that ilemit resolves by reflection; a Kotlin-user base stays a bare type name.
		val baseJson = base?.let {
			val bt = birType(baseType!!)
			// A .NET base: an @Clr-injected type, or a Kotlin stdlib type birType maps to .NET (`Exception` -> clr:
			// System.Exception, etc.). Otherwise a Kotlin-user base stays a bare type name.
			if (clrName(it) != null || bt.startsWith("clr:") || bt.startsWith("clrg:")) str(bt) else str(typeName(it))
		} ?: "null"
		// Stdlib interface supertypes (Iterator, Read(Write)Property) -> their monomorphized synthetic interfaces;
		// a user generic interface `Container<Int>` -> the constructed spec `Container[int]` (ownerSpec).
		val ifaces = klass.superTypes
			.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
			.mapNotNull { st ->
				// A stdlib interface birType maps to .NET (Continuation, Comparable, Comparator, AutoCloseable, …) ->
				// its clr:/clrg: spec; the Kotlin iterator/iterable protocol -> a synthetic interface; a user generic
				// interface `Container<Int>` -> the constructed spec `Container[int]` (ownerSpec).
				// The Kotlin iterator/iterable protocol -> a synthetic monomorphized interface, checked BEFORE birType:
				// `Iterable<T>` as a parameter type lowers to IEnumerable<T> (birType), but as a user class SUPERTYPE it
				// must stay the synthetic KIterable — implementing IEnumerable<T> would demand a synthesized GetEnumerator
				// (the producing-side bridge, separate work). `for (x in r)` over the synthetic interface still works.
				val synthIter = iteratorElemIface(st) ?: iterableElemIface(st)
				if (synthIter != null) synthIter
				else {
					val bt = birType(st)
					if (bt.startsWith("func:")) null
					else if (bt.startsWith("clr:") || bt.startsWith("clrg:")) bt
					else charSeqIface(st) ?: propIface(st) ?: (st.classifierOrNull?.owner as? IrClass)?.let { ownerSpec(it, st) }
				}
			}
			.joinToString(",") { str(it) }
		// Anonymous objects (lifted, tracked in anonNames) are synthetic -> keep public.
		val vis = if (anonNames.containsKey(klass)) "public" else visOf(klass)
		val isAbstract = klass.modality == Modality.ABSTRACT || klass.modality == Modality.SEALED
		// A `nested`/`inner` class is emitted as a true CLR nested type of its enclosing user class (`Outer+Inner`),
		// so it retains Kotlin's access to the enclosing class's private members (instead of flattening to a separate
		// top-level type, which forced an assembly-visibility workaround). `inner` additionally captures `__outer`.
		val nestedIn = emittedNestedParent(klass)?.takeIf { (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE || it.kind == ClassKind.OBJECT || it.kind == ClassKind.ANNOTATION_CLASS) && clrName(it) == null && !anonNames.containsKey(klass) }
			?.let { ""","nestedIn":${str(typeName(it))}""" } ?: ""
		return """{"name":${str(typeName(klass))},"kind":"class","abstract":$isAbstract,"vis":${str(vis)}$nestedIn${typeParamsJson(klass.typeParameters)},"base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods],"attrs":[${attrsJson(klass.annotations)}]}"""
	}

	internal fun ctor(klass: IrClass, ctor: IrConstructor, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
		// Captured outer values arrive as leading ctor params and are stored into the capture fields first
		// (the instance initializers below read them, e.g. `var cur = from` -> `this.__outer.from`).
		val capParams = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		val capAssigns = captures.map { (_, fname) ->
			"""{"k":"setField","ownerType":${str(typeName(klass))},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}"""
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
							stmts.add("""{"k":"setField","ownerType":${str(typeName(klass))},"recv":{"k":"this"},"name":${str(bf.name.asString())},"value":${expr((it as IrExpressionBody).expression)}}""")
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
		if (fn.isSuspend) return suspendMethod(fn, static)
		// An override of a CLASS or ENUM_CLASS member (the latter: a per-entry enum body overriding an abstract enum
		// member) reuses the base virtual slot. (Interface members bind by name/signature, handled elsewhere.)
		val isOverride = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind.let { k -> k == ClassKind.CLASS || k == ClassKind.ENUM_CLASS } }
		val isVirtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT || clrIfaceMemberName(fn) != null
		// An extension function `fun T.f()` -> static method whose first param `__self` is the receiver;
		// body references to the receiver resolve to `__self` (via valSubst).
		val extRecv = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
		// Promote captured-mutated `var`s to ref-cells; accumulate (a nested closure inherits the enclosing set).
		val savedRefCells = refCellVars
		refCellVars = refCellVars + computeRefCells(fn)
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		refCellVars = savedRefCells
		if (extRecv != null) selfSubst.remove(extRecv)
		val selfParam = extRecv?.let { """{"name":"__self","type":${str(birType(it.type))}}""" }
		val ps = (listOfNotNull(selfParam) + paramsJsonList(fn.parameters)).joinToString(",")
		// `override fun toString()/equals()/hashCode()` -> System.Object.ToString/Equals/GetHashCode so that
		// CLR virtual dispatch (Console.WriteLine, structural `==`) finds the override.
		val objName = objectMethodName(fn)
		val clrIfaceName = clrIfaceMemberName(fn)   // e.g. resumeWith -> ResumeWith when implementing Continuation<T>
		val emitName = clrIfaceName ?: objName ?: fn.name.asString()
		val isOvr = isOverride || objName != null || clrIfaceName != null
		// Object-overrides / interface members must stay public for virtual dispatch.
		val vis = if (objName != null || clrIfaceName != null) "public" else visOf(fn)
		val isAbstract = fn.modality == Modality.ABSTRACT && fn.body == null
		// Kotlin modifiers with no .NET analog -> stamped as [KotlinFunction] by ilemit so a consuming Kotlin module
		// can restore them (infix/operator call resolution). `final/open/abstract` ride .NET virtual-ness already.
		val kmods = kotlinModsJson(fn)
		// A user `inline fun` that takes a lambda param: ilemit additionally stamps [KotlinInlineBody] with this body
		// (this method def IS the body), so a consuming module can splice it at the call site — the only way a
		// cross-module non-local `return` through the lambda can work (DotKt inlines at emit, needing the body).
		val inlineFlag = if (isInlineWithLambda(fn)) ""","inline":true""" else ""
		// Return nullability (`fun f(): String?`) — the params carry their own `nullable` flag; ilemit stamps both as .NET NRT ([Nullable]/[NullableContext]).
		val retNull = if (fn.returnType.isMarkedNullable()) ""","retNullable":true""" else ""
		return """{"name":${str(emitName)},"static":$static,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${objName != null},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}$kmods$inlineFlag$retNull,"params":[$ps],"ret":${str(birType(fn.returnType))},"body":[$body],"attrs":[${attrsJson(fn.annotations)}]}"""
	}

	/** `infix`/`operator` flags as BIR JSON fragments (only emitted when set), shared by the regular + suspend paths. */
	internal fun kotlinModsJson(fn: IrSimpleFunction): String =
		(if (fn.isInfix) ""","infix":true""" else "") + (if (fn.isOperator) ""","operator":true""" else "")

	/** An `inline fun` with at least one (inlinable) lambda parameter — the only inline shape whose body must travel
	 *  for cross-module consumption (lambda-less inline funs degrade to ordinary calls; the JIT inlines those). */
	internal fun isInlineWithLambda(fn: IrSimpleFunction): Boolean =
		fn.isInline && fn.parameters.any { it.kind == IrParameterKind.Regular && !it.isNoinline && birType(it.type).startsWith("func:") }

	// ===== Coroutine (suspend fun) -> CLR-native async state machine (strategy B) =====
	// A `suspend fun f(args): T` lowers to a kickoff `Task<T> f(args)` + a struct IAsyncStateMachine (emitted by
	// ilemit). Here we CPS-linearize the body into a FLAT list of steps so ilemit need not reconstruct control
	// flow: ordinary statements stay as-is (ilemit redirects references to cpsFields onto state-machine fields),
	// suspension points become `coSuspend`, and if/while linearize to `coLabel`/`coGoto`/`coCondGoto`. The lowered
	// FORM is Task/awaiter-based. See docs/coroutine-il.md. Capability bar: linear / loop / branch /
	// direct-suspend-call; try-catch-around-await needs exception regions (E-0.5) -> loud error.
	internal var coState = 0
	internal var coLabelN = 0
	internal var coFields: Set<String> = emptySet()

	// await spilling (D): a nested suspending call -> a fresh state-machine field holding its result, plus a
	// suspension step assigning it. coSpill maps the call node to that field so expr() renders a field reference
	// instead of the call; coSpillFields accumulates (field, type) to declare alongside the params/live-vars.
	internal val coSpill = java.util.IdentityHashMap<IrCall, String>()
	internal val coSpillFields = ArrayList<Pair<String, IrType>>()

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

	internal fun coStmtsOf(e: IrExpression): List<org.jetbrains.kotlin.ir.IrStatement> = when (e) {
		is IrBlock -> e.statements
		is IrComposite -> e.statements
		else -> listOf(e)
	}

	/** Variables declared on any suspension-bearing path -> state-machine fields (survive across resume). */
	internal fun collectCpsVars(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, out: MutableList<IrVariable>) {
		for (s in stmts) when (s) {
			is IrVariable -> out.add(s)
			is IrWhen -> if (containsSuspend(s)) s.branches.forEach { collectCpsVars(coStmtsOf(it.result), out) }
			is IrWhileLoop -> if (containsSuspend(s)) s.body?.let { collectCpsVars(coStmtsOf(it), out) }
			is IrTry -> if (containsSuspend(s)) { collectCpsVars(coStmtsOf(s.tryResult), out); s.catches.forEach { collectCpsVars(coStmtsOf(it.result), out) } }
			is IrBlock -> if (containsSuspend(s)) collectCpsVars(s.statements, out)
			is IrComposite -> if (containsSuspend(s)) collectCpsVars(s.statements, out)
			else -> {}
		}
	}

	/** The `Task<T>` an await/suspend-call awaits: the `.await()` receiver, or the direct suspend call itself. */
	internal fun coAwaitable(call: IrCall): String {
		val callee = call.symbol.owner
		// `kotlinx.coroutines.delay(ms)` -> `Task.Delay((int)ms)` (the awaitable; a non-generic Task -> void result).
		if (callee.fqNameWhenAvailable?.asString() == "kotlinx.coroutines.delay")
			return """{"k":"clrStatic","type":"System.Threading.Tasks.Task","method":"Delay","argTypes":["System.Int32"],"ret":"clr:System.Threading.Tasks.Task","args":[{"k":"conv","to":"int","e":${expr(regularArgs(call).first())}}]}"""
		return if (isAwaitIntrinsic(callee)) expr(extensionReceiver(call) ?: dispatchReceiver(call)!!)
		else expr(call)   // a direct suspend call: its kickoff returns Task<T>
	}

	/** CPS state captured by [emitCoroutineBody] for the caller to assemble the method/lambda JSON. */
	internal class CoroutineBody(val resultType: String, val cpsFields: String, val steps: String)

	/**
	 * CPS-linearize a suspend function/lambda body into (resultType, cpsFields, steps) JSON. Shared by
	 * [suspendMethod] and the suspend-lambda path in [lambda] so both lower identically (`emitCps`/`spillExpr`/
	 * `collectCpsVars` → flat steps + state-machine fields = params + live locals + spill temps).
	 */
	internal fun emitCoroutineBody(fn: IrSimpleFunction): CoroutineBody {
		// Save/restore CPS state: a coroutine body can be lowered NESTED inside another (e.g. a `sequence{}` passed to
		// `yieldAll` inside an enclosing `sequence{}`); without this the inner reset corrupts the outer's state ids.
		val savedState = coState; val savedLabelN = coLabelN
		val savedSpill = HashMap(coSpill); val savedSpillFields = ArrayList(coSpillFields); val savedCoFields = coFields
		coState = 0; coLabelN = 0
		coSpill.clear(); coSpillFields.clear()
		// Include the extension receiver (a `suspend Scope.() -> R` lambda's implicit `$this$...`) as a leading
		// param/field, so receiver references inside the CPS body resolve to a state-machine field (T11). It is also
		// the first entry in lambdaParamsJson, so the SM constructor receives it in the same position. EXCLUDE the
		// SequenceScope receiver: `sequence{}` is the restricted-suspension builder whose scope IS the SM (the
		// receiver is synthetic, not a passed value), and its block is lowered by the sequence-special path.
		val realExtRecv = fn.parameters.filter {
			it.kind == IrParameterKind.ExtensionReceiver && it.type.classFqName?.asString() != "kotlin.sequences.SequenceScope"
		}
		val params = realExtRecv + regularParams(fn)
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val liveVars = ArrayList<IrVariable>()
		if (fn.body?.let { containsSuspend(it) } == true) collectCpsVars(body, liveVars)
		coFields = (params.map { it.name.asString() } + liveVars.map { it.name.asString() }).toSet()
		val steps = ArrayList<String>()
		for (s in body) emitCps(s, fn.returnType, steps)
		if (body.lastOrNull() !is IrReturn) steps.add("""{"k":"coReturn","value":null}""")
		val resultType = if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
		val cpsFields = (params.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" } +
			liveVars.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" } +
			coSpillFields.map { """{"name":${str(it.first)},"type":${str(birType(it.second))}}""" }).joinToString(",")
		// Restore the enclosing coroutine's CPS state (no-op at the top level).
		coState = savedState; coLabelN = savedLabelN
		coSpill.clear(); coSpill.putAll(savedSpill); coSpillFields.clear(); coSpillFields.addAll(savedSpillFields); coFields = savedCoFields
		return CoroutineBody(resultType, cpsFields, steps.joinToString(","))
	}

	/**
	 * Select the Continuation-class coroutine form (Path B) when: `@KCont` (explicit), the fun is generic (needs a
	 * generic SM type), or its body directly uses the raw intrinsic `suspendCoroutineUninterceptedOrReturn` (a leaf
	 * that hands out its own continuation — the struct/Task form can't). Ordinary suspend funs stay the struct/Task
	 * form (and just await the leaves' Tasks), keeping that path's IsCompleted fast-path. See docs §13e A1.
	 */
	internal fun isCoClass(fn: IrSimpleFunction): Boolean =
		fn.annotations.any { it.type.classFqName?.shortName()?.asString() == "KCont" } ||
			fn.typeParameters.isNotEmpty() ||
			(fn.body?.let { bodyUsesSuspendIntrinsic(it) } == true)

	/** True if `e` directly calls `suspendCoroutineUninterceptedOrReturn` (not inside a nested lambda/local fun). */
	internal fun bodyUsesSuspendIntrinsic(e: org.jetbrains.kotlin.ir.IrElement): Boolean {
		var found = false
		e.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				if (found) return
				if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
				if (element is IrCall && (isSuspendIntrinsic(element) || isSuspendCancellable(element) ||
						element.symbol.owner.correspondingPropertySymbol?.owner?.fqNameWhenAvailable?.asString() == "kotlin.coroutines.coroutineContext")) { found = true; return }
				element.acceptChildrenVoid(this)
			}
		})
		return found
	}

	internal fun suspendMethod(fn: IrSimpleFunction, static: Boolean): String {
		// An extension `suspend fun T.f()` -> a static kickoff whose first param `__self` is the receiver, captured
		// into the state machine like any other param; receiver references (`<this>`) resolve to `__self`.
		val extRecv = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		if (extRecv != null) selfSubst[extRecv] = """{"k":"local","name":"__self"}"""
		val co = emitCoroutineBody(fn)
		if (extRecv != null) selfSubst.remove(extRecv)
		val selfJson = extRecv?.let { """{"name":"__self","type":${str(birType(it.type))}}""" }
		val ps = (listOfNotNull(selfJson) + paramsJsonList(fn.parameters)).joinToString(",")
		val cps = (listOfNotNull(selfJson) + listOf(co.cpsFields).filter { it.isNotEmpty() }).joinToString(",")
		val vis = visOf(fn)
		val coClass = if (isCoClass(fn)) ""","coClass":true""" else ""
		return """{"name":${str(fn.name.asString())},"static":$static,"override":false,"virtual":false,"objectOverride":false,"vis":${str(vis)}${typeParamsJson(fn.typeParameters)}${kotlinModsJson(fn)},"suspend":true,"resultType":${str(co.resultType)}$coClass,"cpsFields":[$cps],"params":[$ps],"steps":[${co.steps}]}"""
	}

	/**
	 * A suspend lambda body is "trivial" when it is a single tail suspend call (`{ f() }` / `{ return f() }`)
	 * whose arguments don't themselves suspend: the kickoff Task is just forwarded, so no state machine is needed
	 * and the body emits as-is. Anything else needs CPS lowering (Phase 0). See [lambda].
	 */
	internal fun isTrivialSuspendLambda(fn: IrSimpleFunction): Boolean {
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val single = stmts.singleOrNull() ?: return false
		val e = when (single) { is IrReturn -> single.value; is IrExpression -> single; else -> return false }
		return e is IrCall && e.symbol.owner.isSuspend && e.let { call ->
			regularArgs(call).none { containsSuspend(it) } &&
				(extensionReceiver(call) ?: dispatchReceiver(call))?.let { !containsSuspend(it) } ?: true
		}
	}

	internal fun coFresh(): String = "__cor${coLabelN++}"

	internal fun emitCps(stmt: org.jetbrains.kotlin.ir.IrElement, ret: IrType, steps: MutableList<String>) {
		when (stmt) {
			is IrVariable -> {
				val init = stmt.initializer
				when {
					init != null && scopeSuspendCall(init) != null -> emitCpsValue(init, ret, steps, stmt.name.asString(), false)
					init != null && isSuspensionCall(init) -> emitSuspend(init as IrCall, stmt.name.asString(), steps)
					init != null && containsSuspend(init) -> { spillExpr(init, steps); steps.add(stmt(stmt)) }
					else -> steps.add(stmt(stmt))   // sync var; ilemit redirects a cpsField name to a field store
				}
			}
			is IrReturn -> {
				val v = stmt.value
				when {
					scopeSuspendCall(v) != null -> emitCpsValue(v, ret, steps, null, true)
					isSuspensionCall(v) -> {
						val t = coFresh()
						emitSuspend(v as IrCall, t, steps)
						steps.add(coReturnJson(ret, """{"k":"local","name":${str(t)}}"""))
					}
					containsSuspend(v) -> {
						spillExpr(v, steps)
						if (ret.isUnit() || v.type.isUnit()) {
							steps.add("""{"k":"exprStmt","expr":${expr(v)}}"""); steps.add("""{"k":"coReturn","value":null}""")
						} else steps.add(coReturnJson(ret, expr(v)))
					}
					ret.isUnit() || v.type.isUnit() -> steps.add("""{"k":"coReturn","value":null}""")
					else -> steps.add(coReturnJson(ret, expr(v)))
				}
			}
			is IrWhen -> if (containsSuspend(stmt)) emitWhenCps(stmt, ret, steps) else steps.add(stmt(stmt))
			is IrWhileLoop -> if (containsSuspend(stmt)) emitWhileCps(stmt, ret, steps) else steps.add(stmt(stmt))
			is IrBlock -> if (containsSuspend(stmt)) stmt.statements.forEach { emitCps(it, ret, steps) } else steps.add(stmt(stmt))
			is IrComposite -> if (containsSuspend(stmt)) stmt.statements.forEach { emitCps(it, ret, steps) } else steps.add(stmt(stmt))
			is IrCall -> when {
				scopeSuspendCall(stmt) != null -> emitScopeCps(scopeSuspendCall(stmt)!!, ret, steps, null, false)   // discard result
				isSuspensionCall(stmt) -> emitSuspend(stmt, null, steps)
				containsSuspend(stmt) -> { spillExpr(stmt, steps); steps.add("""{"k":"exprStmt","expr":${expr(stmt)}}""") }
				else -> steps.add("""{"k":"exprStmt","expr":${expr(stmt)}}""")
			}
			is IrSetValue -> if (containsSuspend(stmt)) { spillExpr(stmt, steps); steps.add(stmt(stmt)) } else steps.add(stmt(stmt))
			// A type-operator wrapper around a suspension (e.g. `b.await()` discarded -> IMPLICIT_COERCION_TO_UNIT,
			// or an implicit cast from a generic substitution) -> recurse on the inner expression.
			is IrTypeOperatorCall -> if (containsSuspend(stmt)) emitCps(stmt.argument, ret, steps) else steps.add(stmt(stmt))
			is IrTry -> if (containsSuspend(stmt)) emitTryCps(stmt, ret, steps) else steps.add(stmt(stmt))
			else -> {
				if (stmt is IrExpression && containsSuspend(stmt)) steps.add(coUnsupported("suspension in an unsupported position (${stmt::class.simpleName})"))
				else steps.add(stmt(stmt as? org.jetbrains.kotlin.ir.IrStatement ?: return))
			}
		}
	}

	internal fun coReturnJson(ret: IrType, value: String): String =
		if (ret.isUnit()) """{"k":"coReturn","value":null}""" else """{"k":"coReturn","value":$value}"""

	/** Emit `assignTo = e` (assignTo a CPS field name) — or `return e` (assignTo==null, isReturn) — as coroutine steps,
	 *  routing a suspending value (incl. a scope-function call) through the state machine. Shared by emitCps/emitScopeCps. */
	internal fun emitCpsValue(e: IrExpression, ret: IrType, steps: MutableList<String>, assignTo: String?, isReturn: Boolean) {
		fun store(json: String) = when {
			assignTo != null -> steps.add("""{"k":"var","name":${str(assignTo)},"type":${str(birType(e.type))},"init":$json}""")
			ret.isUnit() || e.type.isUnit() -> { steps.add("""{"k":"exprStmt","expr":$json}"""); steps.add("""{"k":"coReturn","value":null}""") }
			else -> steps.add(coReturnJson(ret, json))
		}
		when {
			scopeSuspendCall(e) != null -> emitScopeCps(scopeSuspendCall(e)!!, ret, steps, assignTo, isReturn)
			isSuspensionCall(e) -> {
				// `add(plain())`: spill the suspending args/receiver first (no-op if none), THEN await the call itself.
				spillExpr(e, steps)
				if (assignTo != null) emitSuspend(e as IrCall, assignTo, steps)
				else { val t = coFresh(); emitSuspend(e as IrCall, t, steps); store0Local(t, e.type, ret, steps, isReturn) }
			}
			containsSuspend(e) -> { spillExpr(e, steps); store(expr(e)) }
			else -> store(expr(e))
		}
	}

	/** Return/forward an already-spilled suspend temp `t` (only the return case reaches here). */
	internal fun store0Local(t: String, ty: IrType, ret: IrType, steps: MutableList<String>, isReturn: Boolean) {
		if (ret.isUnit() || ty.isUnit()) steps.add("""{"k":"coReturn","value":null}""")
		else steps.add(coReturnJson(ret, """{"k":"local","name":${str(t)}}"""))
	}

	/** Inline a suspend-bearing scope function (`with(c){…}`, `c.run/let/apply/also {…}`) into the coroutine step list:
	 *  bind the receiver to a CPS field, substitute `this`/`it`, linearize the lambda body (so inner suspensions become
	 *  real await steps), then hand the result (last expr, or the receiver for apply/also) to emitCpsValue. */
	internal fun emitScopeCps(sc: Triple<String, IrExpression, IrFunctionExpression>, ret: IrType, steps: MutableList<String>, assignTo: String?, isReturn: Boolean) {
		val (fq, recvExpr, lambda) = sc
		val fn = lambda.function
		val vname = "__coscope${coLabelN++}"
		coSpillFields.add(vname to recvExpr.type); coFields = coFields + vname   // a CPS field: survives the body's suspensions
		if (containsSuspend(recvExpr)) spillExpr(recvExpr, steps)
		steps.add("""{"k":"var","name":${str(vname)},"type":${str(birType(recvExpr.type))},"init":${expr(recvExpr)}}""")
		val recvParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		recvParam?.let { selfSubst[it] = """{"k":"local","name":${str(vname)}}""" }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val returnsRecv = fq == "kotlin.apply" || fq == "kotlin.also"
		if (returnsRecv) {
			stmts.forEach { if (it !is IrReturn) emitCps(it, ret, steps) }
			recvParam?.let { selfSubst.remove(it) }; itParam?.let { valSubst.remove(it.name.asString()) }
			// result = the receiver field
			val recvLocal = """{"k":"local","name":${str(vname)}}"""
			when {
				assignTo != null -> steps.add("""{"k":"var","name":${str(assignTo)},"type":${str(birType(recvExpr.type))},"init":$recvLocal}""")
				isReturn -> steps.add(coReturnJson(ret, recvLocal))
			}
		} else {
			stmts.dropLast(1).forEach { emitCps(it, ret, steps) }
			val last = stmts.lastOrNull()
			val lastE = when (last) { is IrReturn -> last.value; is IrExpression -> last; else -> { last?.let { emitCps(it, ret, steps) }; null } }
			if (lastE != null) emitCpsValue(lastE, ret, steps, assignTo, isReturn)
			else if (isReturn) steps.add("""{"k":"coReturn","value":null}""")
			recvParam?.let { selfSubst.remove(it) }; itParam?.let { valSubst.remove(it.name.asString()) }
		}
	}

	internal fun coUnsupported(of: String): String = """{"k":"coUnsupported","of":${str(of)}}"""

	/**
	 * await spilling: hoist every nested suspending sub-call of `e` into its own state-machine field + suspension
	 * step, in left-to-right evaluation order, so the residual `e` (re-rendered via expr(), which consults coSpill)
	 * is suspension-free. Post-order = a call's receiver/args spill before the call itself, so `f(a.await()).await()`
	 * and `a.await() + b.await()` both linearize correctly. Each spilled value lives in a field because another
	 * suspension may follow before the residual reads it (e.g. `a.await() + b.await()` resumes twice).
	 */
	internal fun spillExpr(e: org.jetbrains.kotlin.ir.IrElement, steps: MutableList<String>) {
		e.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				// Don't spill suspensions that live inside a nested lambda / local fun (a separate coroutine).
				if (element is IrFunctionExpression || element is org.jetbrains.kotlin.ir.declarations.IrFunction) return
				element.acceptChildrenVoid(this)   // receiver/args (earlier in eval order) spill first
				if (element is IrCall && isSuspensionCall(element) && !coSpill.containsKey(element)) {
					val t = coFresh()
					emitSuspend(element, t, steps)   // coAwaitable() reads already-spilled inner temps via expr()
					coSpill[element] = t
					coSpillFields.add(t to element.type)
				}
			}
		})
	}

	/** The raw `kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn { c -> ... }` leaf intrinsic. */
	internal fun isSuspendIntrinsic(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
		e is IrCall && e.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn"

	/** `SequenceScope.yield(value)` inside a `sequence { … }` builder — a multi-shot (restricted) suspension. */
	internal fun isYield(call: IrCall): Boolean =
		call.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.sequences.SequenceScope.yield"

	/** `SequenceScope.yieldAll(elements)` — yield every element of an Iterable/Sequence (lowered as an inner
	 *  enumerator loop in the sequence state machine). */
	internal fun isYieldAll(call: IrCall): Boolean =
		call.symbol.owner.fqNameWhenAvailable?.asString() == "kotlin.sequences.SequenceScope.yieldAll"

	/** `kotlinx.coroutines.suspendCancellableCoroutine { c -> … }` — like the raw intrinsic but `c` is a
	 *  CancellableContinuation and the block ALWAYS suspends (returns Unit, not the sentinel). */
	internal fun isSuspendCancellable(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
		e is IrCall && e.symbol.owner.fqNameWhenAvailable?.asString() == "kotlinx.coroutines.suspendCancellableCoroutine"

	/** A suspension point: start the awaitable; if incomplete, save state and return; on resume read the result. */
	internal fun emitSuspend(call: IrCall, assignTo: String?, steps: MutableList<String>) {
		if (isSuspendIntrinsic(call)) { emitSuspendIntrinsic(call, assignTo, steps, alwaysSuspend = false, selfKind = "coSelfCont"); return }
		if (isSuspendCancellable(call)) { emitSuspendIntrinsic(call, assignTo, steps, alwaysSuspend = true, selfKind = "coSelfCancellable"); return }
		if (isYield(call)) { val k = ++coState; steps.add("""{"k":"coYield","state":$k,"value":${expr(regularArgs(call).first())}}"""); return }
		if (isYieldAll(call)) {
			val k = ++coState
			val arg = regularArgs(call).first()   // Iterable<T>/Sequence<T> -> IEnumerable<T>
			steps.add("""{"k":"coYieldAll","state":$k,"iterable":${expr(arg)},"iterType":${str(birType(arg.type))}}""")
			return
		}
		val k = ++coState
		steps.add("""{"k":"coSuspend","state":$k,"awaitable":${coAwaitable(call)},"assignTo":${assignTo?.let { str(it) } ?: "null"},"resultType":${str(birType(call.type))}}""")
	}

	/**
	 * The raw suspension intrinsics: the block receives the coroutine's OWN continuation. For
	 * `suspendCoroutineUninterceptedOrReturn` (alwaysSuspend=false) the block returns a value (resume synchronously)
	 * or COROUTINE_SUSPENDED; for `suspendCancellableCoroutine` (alwaysSuspend=true) it returns Unit and always
	 * suspends. We inline the block: bind `c` to the SM itself (`selfKind` = a typed adapter over `this`), emit the
	 * body statements, and carry the result into a `coSuspendIntrinsic` step. Continuation-class form only.
	 */
	internal fun emitSuspendIntrinsic(call: IrCall, assignTo: String?, steps: MutableList<String>, alwaysSuspend: Boolean, selfKind: String) {
		val k = ++coState
		val resultT = birType(call.type)
		val block = regularArgs(call).firstOrNull() as? IrFunctionExpression
			?: run { steps.add(coUnsupported("a raw suspension intrinsic without a literal block")); return }
		val cParam = block.function.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		if (cParam != null) captureSubst[cParam] = """{"k":"$selfKind","resultType":${str(resultT)}}"""
		val pre = ArrayList<String>()
		var valueExpr = """{"k":"coSuspendedSentinel"}"""
		for (s in (block.function.body as? IrBlockBody)?.statements.orEmpty()) {
			when {
				// alwaysSuspend: the block returns Unit — keep its (possibly side-effecting) tail as a pre-statement.
				alwaysSuspend && s is IrReturn -> s.value?.takeIf { !it.type.isUnit() }?.let { pre.add("""{"k":"exprStmt","expr":${expr(it)}}""") }
				s is IrReturn -> valueExpr = expr(s.value)
				else -> pre.add(stmt(s))
			}
		}
		if (cParam != null) captureSubst.remove(cParam)
		steps.add("""{"k":"coSuspendIntrinsic","state":$k,"pre":[${pre.joinToString(",")}],"value":$valueExpr,"assignTo":${assignTo?.let { str(it) } ?: "null"},"resultType":${str(resultT)}}""")
	}

	internal fun emitCpsBlock(e: IrExpression, ret: IrType, steps: MutableList<String>) =
		coStmtsOf(e).forEach { emitCps(it, ret, steps) }

	internal fun emitWhenCps(w: IrWhen, ret: IrType, steps: MutableList<String>) {
		val end = coLabelN++
		for (branch in w.branches) {
			val isElse = branch.condition.let { it is IrConst && it.value == true }
			if (isElse) {
				emitCpsBlock(branch.result, ret, steps)
				steps.add("""{"k":"coGoto","id":$end}""")
			} else {
				if (containsSuspend(branch.condition)) spillExpr(branch.condition, steps)   // await in the condition -> spilled before the test
				val next = coLabelN++
				steps.add("""{"k":"coCondGoto","id":$next,"cond":${expr(branch.condition)}}""")
				emitCpsBlock(branch.result, ret, steps)
				steps.add("""{"k":"coGoto","id":$end}""")
				steps.add("""{"k":"coLabel","id":$next}""")
			}
		}
		steps.add("""{"k":"coLabel","id":$end}""")
	}

	/**
	 * try/catch around a suspension point (D capstone). The flat step list carries `coTryBegin`/`coCatchBegin`/
	 * `coTryEnd` markers; ilemit turns them into a `.try`/catch with a TWO-LEVEL dispatch (outer dispatch enters
	 * the try, inner dispatch resumes at the suspension inside it) + a single-exit MoveNext (suspension/return use
	 * `leave` not `ret`). v1 scope: catch clauses only (no `finally`), and the suspension must be in the TRY body
	 * (not inside a catch) — both else clean `coUnsupported` (resume-into-catch / finally-aware leave deferred).
	 */
	internal fun emitTryCps(t: IrTry, ret: IrType, steps: MutableList<String>) {
		val hasFinally = t.finallyExpression != null
		// `finally` around a suspension is NOT a CLR finally clause (a suspend `leave`s the .try, which would run a
		// real finally on every suspend). Instead ilemit emits the finally body explicitly on the normal-exit path
		// AND in a synthesized catch-all that rethrows (T10 / docs §13v). v1: finally only when there are no `catch`
		// clauses, and neither the finally nor any catch suspends.
		if (hasFinally && t.catches.isNotEmpty()) { steps.add(coUnsupported("try with both catch and finally around a suspension")); return }
		if (hasFinally && containsSuspend(t.finallyExpression!!)) { steps.add(coUnsupported("a suspension inside a finally")); return }
		if (t.catches.any { containsSuspend(it.result) }) { steps.add(coUnsupported("suspension inside a catch clause")); return }
		val tid = coLabelN++
		steps.add("""{"k":"coTryBegin","id":$tid}""")
		emitCpsBlock(t.tryResult, ret, steps)
		for (c in t.catches) {
			val v = c.catchParameter.name.asString()
			steps.add("""{"k":"coCatchBegin","id":$tid,"excType":${str(netType(c.catchParameter.type))},"var":${str(v)}}""")
			emitCpsBlock(c.result, ret, steps)
		}
		val finallyJson = if (hasFinally) {
			val fin = ArrayList<String>(); emitCpsBlock(t.finallyExpression!!, ret, fin)
			""","finally":[${fin.joinToString(",")}]"""
		} else ""
		steps.add("""{"k":"coTryEnd","id":$tid$finallyJson}""")
	}

	internal fun emitWhileCps(loop: IrWhileLoop, ret: IrType, steps: MutableList<String>) {
		val start = coLabelN++; val end = coLabelN++
		steps.add("""{"k":"coLabel","id":$start}""")
		// await in the condition -> spilled AFTER the loop-start label, so it re-runs (re-suspends) each iteration.
		if (containsSuspend(loop.condition)) spillExpr(loop.condition, steps)
		steps.add("""{"k":"coCondGoto","id":$end,"cond":${expr(loop.condition)}}""")
		loop.body?.let { emitCpsBlock(it, ret, steps) }
		steps.add("""{"k":"coGoto","id":$start}""")
		steps.add("""{"k":"coLabel","id":$end}""")
	}

	/**
	 * `,"typeParams":[...]` for a generic class/interface/method (empty when non-generic). An unconstrained param
	 * is a bare name string `"T"`; a bounded one (`<T : Comparable<T>>`) is `{"name":"T","constraints":[...]}`
	 * (each constraint a BIR type, e.g. `clrg:System.IComparable[gp:T]`). `kotlin.Any` bounds are dropped.
	 */
	internal fun typeParamsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String {
		if (tps.isEmpty()) return ""
		val entries = tps.joinToString(",") { tp ->
			val bounds = tp.superTypes.filter { it.classFqName?.asString() != "kotlin.Any" }.map { birType(it) }
			// Declaration-site variance `out`/`in` -> CLR covariant/contravariant (ilemit applies it only on
			// interfaces, where the CLR allows variance; on classes it's Kotlin-level only — dropped).
			val variance = when (tp.variance) {
				org.jetbrains.kotlin.types.Variance.OUT_VARIANCE -> "out"
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
	internal fun ownerSpec(klass: IrClass?, recvType: IrType?): String {
		klass ?: return "?"
		// CharSequence (declaring class of a call on a CharSequence-typed value) -> the synthetic interface name.
		if (klass.fqNameWhenAvailable?.asString() == "kotlin.CharSequence") { usesCharSeq = true; return "<>dotkt_CharSequence" }
		val name = typeName(klass)
		if (klass.typeParameters.isEmpty()) return name
		val args = (recvType as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }
		// A type-parameter argument is emitted via its `gp:T` form (resolvable by ilemit in the enclosing generic
		// method/class context) — NOT dropped to the raw open type, which would make `new State<T>(i)` inside a
		// generic factory `fun <T> state(i:T): State<T>` emit a `newobj` on the open generic (invalid IL; item 13).
		if (args.isNullOrEmpty()) return name
		return "$name[${args.joinToString(",") { birType(it) }}]"
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
		val clrOwner = acc.overriddenSymbols.asSequence()
			.map { it.owner }.map { (if (it.isFakeOverride) it.resolveFakeOverride() else it)?.parent as? IrClass }
			.mapNotNull { it?.let(::clrName) }.firstOrNull() ?: return null
		val isGetter = acc == prop.getter
		val netName = clrName(prop) ?: prop.name.asString()
		val emitName = (if (isGetter) "get_" else "set_") + netName
		val body = (acc.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		val ps = acc.parameters.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		val ret = if (isGetter) birType(acc.returnType) else "void"
		return """{"name":${str(emitName)},"static":false,"override":true,"virtual":true,"objectOverride":false,"clrOverride":${str(clrOwner)},"vis":"public","params":[$ps],"ret":${str(ret)},"body":[$body]}"""
	}

	internal fun paramsJsonList(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): List<String> =
		params.filter { isValueParameter(it) }
			.map {
				// `vararg xs: T` -> mark the param so ilemit stamps [ParamArray] (native .NET varargs; a cross-module
				// consumer can then call `f(1, 2, 3)`). A nullable type rides a `nullable` flag (ref types are nullable
				// in IL anyway; the flag is for the consumer's FIR to restore `T?`).
				val vararg = if (it.varargElementType != null) ""","vararg":true""" else ""
				val nullable = if (it.type.isMarkedNullable()) ""","nullable":true""" else ""
				// A CONSTANT default arg -> carry it so ilemit stamps [DefaultParameterValue]; a cross-module caller can
				// then omit the arg (ilemit's EmitDefaultArg fills it from the .NET metadata). Non-const defaults are dropped.
				val default = (it.defaultValue?.expression as? org.jetbrains.kotlin.ir.expressions.IrConst)?.let { c -> ""","default":${expr(c)}""" } ?: ""
				"""{"name":${str(it.name.asString())},"type":${str(birType(it.type))}$vararg$nullable$default}"""
			}

	/** A `,"sig":"<paramtypes>"` field carried on a call so ilemit resolves the right OVERLOAD by name+signature. Emit
	 *  it ALWAYS: for a non-overloaded callee it's harmless (ilemit's `MethodsBySig` lookup hits the sole method, or
	 *  falls back to the name), and emitting unconditionally avoids any overload-detection edge case. The signature
	 *  MATCHES how `method()` lays out the def's `params` ([ext receiver?] + regular params, each `birType`). */
	internal fun overloadSigField(fn: org.jetbrains.kotlin.ir.declarations.IrFunction): String {
		val ext = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }?.let { birType(it.type) }
		val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { birType(it.type) }
		return ""","sig":${str((listOfNotNull(ext) + regs).joinToString(","))}"""
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
		// `for (x in array)` -> an indexed loop (avoids the kotlin iterator types).
		if (source != null && isArrayType(source.type))
			return """{"k":"forArray","label":$lbl,"var":${str(loopVar.name.asString())},"elem":${str(arrayElemType(source.type))},"array":${expr(source)},"body":[$body]}"""
		// A `for` over a kotlin.* collection is NOT intercepted: FIR already desugared it to the iterator protocol
		// (`it = coll.iterator(); while (it.hasNext()) { x = it.next(); … }`). Returning null here lets that block emit
		// as ordinary kotlin.* calls — no BCL IEnumerator lowering. Only CLR-native shapes (array/range) + injected .NET
		// enumerables stay special-cased.
		// `for (x in dotNetEnumerable)` -> enumerate any .NET IEnumerable<T> (@Clr type) via GetEnumerator
		// (forEachInline). This runs only after the frontend has resolved an iterator operation from source/stdlib
		// declarations; the FIR injector no longer synthesizes Kotlin's iterator protocol for .NET types.
		// Element type = the source's first type arg (e.g. Collection<Int> -> Int), else the loop var's type.
		if (source != null && (source.type.classifierOrNull?.owner as? IrClass)?.let { clrName(it) } != null) {
			val elem = (source.type as? IrSimpleType)?.arguments?.firstOrNull()
				?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: birType(loopVar.type)
			return """{"k":"forEachInline","label":$lbl,"elem":${str(elem)},"src":${expr(source)},"var":${str(loopVar.name.asString())},"body":[$body]}"""
		}
		val range = source as? IrCall ?: return null
		val ops = range.arguments.filterNotNull()
		if (ops.size != 2) return null
		val (cmp, step) = when (range.symbol.owner.name.asString()) {
			"rangeTo" -> "<=" to 1
			"until", "rangeUntil" -> "<" to 1
			"downTo" -> ">=" to -1
			else -> return null
		}
		return """{"k":"for","label":$lbl,"var":${str(loopVar.name.asString())},"from":${expr(ops[0])},"to":${expr(ops[1])},"cmp":${str(cmp)},"step":$step,"body":[$body]}"""
	}

	internal fun tryStmt(node: IrTry): String {
		val catches = node.catches.joinToString(",") { c ->
			val p = c.catchParameter
			// birType (not netType) so a USER exception class catches as its own type (`@AppErr`), not `object` —
			// netType has no mapping for user classes and degrades to System.Object (unverifiable catch).
			"""{"excType":${str(birType(p.type))},"var":${str(p.name.asString())},"body":[${bodyStmts(c.result)}]}"""
		}
		val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
		return """{"k":"try","type":${str(birType(node.type))},"body":[${bodyStmts(node.tryResult)}],"catches":[$catches]$finally}"""
	}

	internal fun bodyStmts(e: IrExpression): String =
		if (e is IrBlock) e.statements.joinToString(",") { stmt(it) } else stmt(e)

	/** `try`/`catch` in value position -> a temp local assigned in each branch, returned via a valueBlock. */
	internal fun tryExpr(node: IrTry): String {
		val tv = "<>dotkt_tryval${scopeCounter++}"
		val tryBody = bodyStmtsAssign(node.tryResult, tv)
		val catches = node.catches.joinToString(",") { c ->
			val p = c.catchParameter
			"""{"excType":${str(netType(p.type))},"var":${str(p.name.asString())},"body":[${bodyStmtsAssign(c.result, tv)}]}"""
		}
		val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
		val tryS = """{"k":"try","type":"void","body":[$tryBody],"catches":[$catches]$finally}"""
		return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(tv)},"type":${str(birType(node.type))}},$tryS],"result":{"k":"local","name":${str(tv)}}}"""
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
	 * A lambda -> a delegate. Non-capturing lambdas lift to a static method (`delegateNew`); capturing
	 * lambdas synthesize a closure class (fields = captured vars, instance `invoke` method) (`closureNew`).
	 */
	/**
	 * The enclosing type parameters a synthesized closure CLASS must be generic over: those referenced by its capture
	 * field types (and its own parameter/return types). On the CLR generics are reified, so a closure that captures a
	 * `T`-typed value (or a `List<T>` / `(T)->Unit`) becomes a SEPARATE class with a `gp:T` field — and `T` (an
	 * enclosing *method* type parameter) is not in scope from inside that class. The closure class must therefore
	 * declare `T` itself and be instantiated with the enclosing `T` at `closureNew`, or `MapType` fails to resolve it.
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

	internal fun lambda(node: IrFunctionExpression): String {
		val fn = node.function
		// A `suspend () -> T` lambda is a coroutine; in the CLR ABI it is a `Func<Task<T>>` (coroutine-abi-decision).
		// The trivial builder lambda `{ f() }` (a single tail suspend call) just returns f()'s kickoff Task, so the
		// emitted body is correct as-is — only the declared return type / delegate type become Task<T> / Func<Task<T>>.
		val ret = if (fn.isSuspend) coTaskType(fn.returnType) else if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
		val ftype = if (fn.isSuspend) coSuspendFuncType(fn) else funcTypeOf(fn)
		// A non-trivial suspend lambda body is itself a coroutine: CPS-linearize it (Phase 0) so ilemit emits a
		// state machine + Task<T> kickoff for the lifted method / closure `invoke`, exactly like a `suspend fun`.
		// Trivial `{ f() }` lambdas just forward f()'s kickoff Task and emit as-is. See isTrivialSuspendLambda.
		val cps = fn.isSuspend && !isTrivialSuspendLambda(fn)
		// A lambda has no `this` of its own, so a referenced `<this>` is the enclosing instance -> capture it.
		val captures = capturedVars(fn, includeThis = true)
		if (captures.isEmpty()) {
			val lname = "__lambda${lambdaCounter++}"
			val freeTps = freeTypeParams(fn.parameters.map { it.type } + listOf(fn.returnType))
			val typeParams = typeParamsJson(freeTps)
			if (cps) {
				val co = emitCoroutineBody(fn)
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false$typeParams,"suspend":true,"resultType":${str(co.resultType)},"cpsFields":[${co.cpsFields}],"params":[${lambdaParamsJson(fn.parameters)}],"steps":[${co.steps}]}""")
			} else {
				val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false$typeParams,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}""")
			}
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
			return """{"k":"delegateNew","method":${str(lname)},"funcType":${str(ftype)}$typeArgs}"""
		}
		// Capturing: build a closure class. Captures rewrite to `this.<field>` (by symbol identity, so the
		// enclosing `this` — captured when the lambda reads a member — maps to a `__outer` field, not the
		// closure's own `this`). For a CPS suspend lambda the closure `invoke` is an INSTANCE coroutine; ilemit
		// captures the closure `this` into the state machine so resume can still read the captured-var fields.
		val cname = "<>dotkt_${synthScope}_Closure${closureCounter++}"
		val capPairs = captures.map { it to captureFieldName(it) }
		// Save any prior substitution for each captured decl so the OUTER binding (e.g. an intrinsic block's `c`
		// bound to the coroutine's own continuation) is restored after the body — not blown away — so the capture
		// VALUE (capValueExpr below) is still evaluated correctly in the enclosing context.
		val savedSubst = capPairs.associate { (decl, _) -> decl to captureSubst[decl] }
		capPairs.forEach { (decl, fname) ->
			captureSubst[decl] = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		}
		val invoke: String
		if (cps) {
			val co = emitCoroutineBody(fn)
			invoke = """{"name":"invoke","static":false,"override":false,"virtual":false,"suspend":true,"resultType":${str(co.resultType)},"cpsFields":[${co.cpsFields}],"params":[${lambdaParamsJson(fn.parameters)}],"steps":[${co.steps}]}"""
		} else {
			val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
			invoke = """{"name":"invoke","static":false,"override":false,"virtual":false,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}"""
		}
		capPairs.forEach { (decl, _) -> val prev = savedSubst[decl]; if (prev != null) captureSubst[decl] = prev else captureSubst.remove(decl) }
		val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		val ctorBody = capPairs.joinToString(",") { (_, fname) -> """{"k":"setField","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}""" }
		// The closure must be GENERIC over any enclosing type parameters it captures (reified CLR generics — a `gp:T`
		// field is unresolved otherwise). Declare them on the class and pass them as type arguments at `closureNew`.
		val freeTps = freeTypeParams(capPairs.map { it.first.type } + fn.parameters.map { it.type } + listOf(fn.returnType))
		liftedTypes.add("""{"name":${str(cname)},"kind":"class"${typeParamsJson(freeTps)},"base":null,"interfaces":[],"fields":[$fields],"ctors":[{"params":[$fields],"baseArgs":null,"body":[$ctorBody]}],"methods":[$invoke]}""")
		// Capture values are evaluated in the enclosing context (the outer `this`, or an outer local).
		val capExprs = captures.joinToString(",") { capValueExpr(it) }
		val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
		return """{"k":"closureNew","closureType":${str(cname)},"captures":[$capExprs],"method":"invoke","funcType":${str(ftype)}$typeArgs}"""
	}

	/**
	 * A callable reference `::foo` -> a delegate bound to the referenced function. v1 scope: a top-level/static
	 * function reference (no receiver, no bound args) reuses the lambda `delegateNew` path — top-level funs are
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
				val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
				val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retT = birType(ctor.returnType)
				val newE = """{"k":"new","type":${str(ownerSpec(klass, ctor.returnType))},"args":[$argsJson]}"""
				val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
				return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${ps.joinToString(",") { birTypeDeleg(it.type) }}"$typeArgs}"""
			}
			// `::NetType` — a lifted factory `__ctorref(args) = new NetType(args)` (clrNew), bound as a delegate.
			if (klass != null) {
				val ps = ctor.parameters.filter { it.kind == IrParameterKind.Regular }
				val lname = "__ctorref${lambdaCounter++}"
				val psJson = ps.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
				val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retT = birType(ctor.returnType)
				val newE = """{"k":"clrNew","type":${str(clrName(klass)!!)},"argTypes":[${ps.joinToString(",") { str(netType(it.type)) }}],"args":[$argsJson]}"""
				val freeTps = freeTypeParams(ps.map { it.type } + listOf(ctor.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
				return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${ps.joinToString(",") { birTypeDeleg(it.type) }}"$typeArgs}"""
			}
			return unsupported(node, "this constructor reference", "the constructor's class could not be resolved")
		}
		val fn = node.symbol.owner as? IrSimpleFunction
			?: return unsupported(node, "this function reference", "only references to plain (simple) functions are supported")
		val dispatchIdx = fn.parameters.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		val hasExt = fn.parameters.any { it.kind == IrParameterKind.ExtensionReceiver }
		// `::topLevelFun` — no receiver: a delegate over the static file-class method (FindStatic resolves it).
		if (dispatchIdx < 0 && !hasExt)
			return """{"k":"delegateNew","method":${str(fn.name.asString())},"funcType":${str(funcTypeOf(fn))}}"""
		// `obj::method` — a bound instance reference: a delegate whose target is the bound receiver. Only USER
		// classes (the method resolves via FindMethod); .NET-method / extension / unbound refs are deferred.
		val boundRecv = if (dispatchIdx >= 0 && !hasExt) node.arguments.getOrNull(dispatchIdx) else null
		val ownerClass = fn.parent as? IrClass
		if (boundRecv != null && ownerClass != null && clrName(ownerClass) == null) {
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			return """{"k":"boundDelegateNew","ownerType":${str(typeName(ownerClass))},"method":${str(fn.name.asString())},"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${str(funcTypeOf(fn))}}"""
		}
		// `Class::method` (UNbound) -> a lifted static `__mref(self, args) = self.method(args)`; the receiver
		// becomes the delegate's first parameter. User classes only (`Func<UserType,…>` resolves via DelegateCtor).
		if (dispatchIdx >= 0 && boundRecv == null && !hasExt && ownerClass != null && clrName(ownerClass) == null) {
			val selfT = birType(fn.parameters[dispatchIdx].type)
			val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }
			val lname = "__mref${lambdaCounter++}"
			val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
				ps.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }).joinToString(",")
			val argsJson = ps.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
			val virtual = fn.modality != Modality.FINAL || fn.overriddenSymbols.isNotEmpty()
			val callE = """{"k":"callInstance","ownerType":${str(typeName(ownerClass))},"virtual":$virtual,"recv":{"k":"local","name":"__self"},"method":${str(fn.name.asString())},"args":[$argsJson]}"""
			val retVoid = fn.returnType.isUnit()
			val retT = if (retVoid) "void" else birType(fn.returnType)
			val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
			val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + ps.map { it.type } + listOf(fn.returnType))
			val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${(listOf(selfT) + ps.map { birTypeDeleg(it.type) }).joinToString(",")}"$typeArgs}"""
		}
		// A .NET method reference. Bound `obj::m` -> a delegate over the .NET instance method (ldftn). Unbound
		// `NetType::m` -> a lifted static `__mref(self, args) = self.m(args)` via clrInstance.
		val clrOwner = ownerClass?.let { clrName(it) }
		if (clrOwner != null && !hasExt) {
			val regs = fn.parameters.filter { it.kind == IrParameterKind.Regular }
			val argTypes = regs.joinToString(",") { str(netType(it.type)) }
			val member = clrName(fn) ?: fn.name.asString()
			val virtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT
			if (boundRecv != null)
				return """{"k":"boundClrDelegateNew","clrType":${str(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"virtual":$virtual,"recv":${expr(boundRecv)},"funcType":${str(funcTypeOf(fn))}}"""
			if (dispatchIdx >= 0) {
				val selfT = birType(fn.parameters[dispatchIdx].type)
				val lname = "__mref${lambdaCounter++}"
				val psJson = (listOf("""{"name":"__self","type":${str(selfT)}}""") +
					regs.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }).joinToString(",")
				val argsJson = regs.joinToString(",") { """{"k":"local","name":${str(it.name.asString())}}""" }
				val retVoid = fn.returnType.isUnit()
				val retT = if (retVoid) "void" else birType(fn.returnType)
				val callE = """{"k":"clrInstance","type":${str(clrOwner)},"method":${str(member)},"argTypes":[$argTypes],"ret":${str(netType(fn.returnType))},"recv":{"k":"local","name":"__self"},"args":[$argsJson]}"""
				val body = if (retVoid) """{"k":"exprStmt","expr":$callE}""" else """{"k":"return","value":$callE}"""
				val freeTps = freeTypeParams(listOf(fn.parameters[dispatchIdx].type) + regs.map { it.type } + listOf(fn.returnType))
				val typeArgs = if (freeTps.isEmpty()) "" else ""","typeArgs":[${freeTps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false${typeParamsJson(freeTps)},"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
				return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${(listOf(selfT) + regs.map { birTypeDeleg(it.type) }).joinToString(",")}"$typeArgs}"""
			}
		}
		return unsupported(node, "a method reference to a .NET method (`::${fn.name}`)",
			"wrap the call in a lambda instead, e.g. `{ a -> x.${fn.name}(a) }`")
	}

	/** The kickoff/return BIR type for a `suspend (...) -> T`: `Task<T>` (or non-generic `Task` for Unit). */
	internal fun coTaskType(ret: IrType): String =
		if (ret.isUnit()) "clr:System.Threading.Tasks.Task" else "clrg:System.Threading.Tasks.Task[${birType(ret)}]"

	/** The delegate type for a `suspend (P...) -> T`: `Func<P..., Task<T>>` encoded as `func:<Task<T>>:<P...>`. */
	internal fun coSuspendFuncType(fn: IrSimpleFunction): String {
		val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(",") { birTypeDeleg(it.type) }
		return "func:${coTaskType(fn.returnType)}:$ps"
	}

	/**
	 * Inline a scope function `recv.let/run/with/apply/also { ... }` to a value-block: bind the receiver to
	 * a unique local, rewrite `it`/`this` to it, then yield the lambda's last expression (let/run/with) or
	 * the receiver (apply/also). No delegate — the lambda body is spliced in directly.
	 */
	internal fun inlineScope(fq: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
		val fn = lambda.function
		// A suspending call inside a scope-function lambda is CPS-linearized in STATEMENT position (emitScopeCps) — but
		// reaching here means the scope fn is in a SUB-EXPRESSION position (e.g. `c.apply{ s() }.x`), which inlines to a
		// value-block the CPS path can't open. Reject cleanly (rare). Workaround: bind it to a `val` first.
		if (fn.body?.let { containsSuspend(it) } == true)
			return unsupported(lambda, "a suspending call inside a `${fq.substringAfterLast('.')}` scope function used as a sub-expression",
				"bind the scope-function result to a `val` first (it's CPS-linearized in statement position), or extract the body into its own `suspend fun`")
		val vname = "__scope${scopeCounter++}"
		val recvInit = expr(recvExpr)   // emit the receiver expression before binding `it`/`this`
		val recvParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		recvParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val returnsRecv = fq == "kotlin.apply" || fq == "kotlin.also"
		val init = ArrayList<String>()
		init.add("""{"k":"var","name":${str(vname)},"type":${str(birType(recvExpr.type))},"init":$recvInit}""")
		val result: String
		if (returnsRecv) {
			stmts.forEach { if (it !is IrReturn) init.add(stmt(it)) }   // body is side-effects; Unit returns dropped
			result = """{"k":"local","name":${str(vname)}}"""
		} else {
			stmts.dropLast(1).forEach { init.add(stmt(it)) }
			result = when (val last = stmts.lastOrNull()) {
				is IrReturn -> expr(last.value)
				is IrExpression -> expr(last)
				else -> { last?.let { init.add(stmt(it)) }; """{"k":"const","type":"void","value":null}""" }
			}
		}
		recvParam?.let { valSubst.remove(it.name.asString()) }
		itParam?.let { valSubst.remove(it.name.asString()) }
		return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
	}

	/** `r.use { block }` -> a value-block: `var r; var res; try { res = block(r) } finally { r.Dispose() }; res`. */
	internal fun inlineUse(recvExpr: IrExpression, lambda: IrFunctionExpression, retType: String): String {
		val fn = lambda.function
		val uname = "__use${scopeCounter++}"; val rname = "__useRes${scopeCounter++}"
		val recvInit = expr(recvExpr)
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(uname)}}""" }
		val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val unit = retType == "void"
		val tryBody = ArrayList<String>()
		stmts.dropLast(1).forEach { tryBody.add(stmt(it)) }
		when (val last = stmts.lastOrNull()) {
			is IrReturn -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last.value)}}""") else last.value.takeIf { !it.type.isUnit() }?.let { tryBody.add("""{"k":"exprStmt","expr":${expr(it)}}""") }
			is IrExpression -> if (!unit) tryBody.add("""{"k":"setLocal","name":${str(rname)},"value":${expr(last)}}""") else tryBody.add("""{"k":"exprStmt","expr":${expr(last)}}""")
			else -> last?.let { tryBody.add(stmt(it)) }
		}
		itParam?.let { valSubst.remove(it.name.asString()) }
		// close() -> IDisposable.Dispose() (Kotlin (Auto)Closeable maps to IDisposable; callvirt works for any impl).
		val dispose = """{"k":"exprStmt","expr":{"k":"clrInstance","type":"System.IDisposable","method":"Dispose","argTypes":[],"ret":"System.Void","virtual":true,"recv":{"k":"local","name":${str(uname)}},"args":[]}}"""
		val tryNode = """{"k":"try","type":"void","body":[${tryBody.joinToString(",")}],"catches":[],"finally":[$dispose]}"""
		val init = ArrayList<String>()
		init.add("""{"k":"var","name":${str(uname)},"type":${str(birType(recvExpr.type))},"init":$recvInit}""")
		if (!unit) init.add("""{"k":"var","name":${str(rname)},"type":${str(retType)}}""")
		init.add(tryNode)
		val result = if (unit) """{"k":"const","type":"void","value":null}""" else """{"k":"local","name":${str(rname)}}"""
		return """{"k":"valueBlock","stmts":[${init.joinToString(",")}],"result":$result}"""
	}

	internal var synthCounter = 0
	/**
	 * A synthetic one-arg lambda `(__x: paramType) -> bodyOf("__x")` lifted to a static method + delegate. Used for
	 * LINQ ops that need a transform Kotlin doesn't supply as a user lambda (e.g. `chunked` -> `Select(c => c.ToList())`,
	 * `filterNotNull` -> `Where(x => x != null)`). `bodyOf` builds the body expression from the param-ref BIR.
	 */
	internal fun synthLambda(paramType: String, retType: String, bodyOf: (String) -> String): String {
		val lname = "__synth${synthCounter++}"
		val pref = """{"k":"local","name":"__x"}"""
		liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false,"params":[{"name":"__x","type":${str(paramType)}}],"ret":${str(retType)},"body":[{"k":"return","value":${bodyOf(pref)}}]}""")
		return """{"k":"delegateNew","method":${str(lname)},"funcType":${str("func:$retType:$paramType")}}"""
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
		val subKeys = ArrayList<String>()
		val calleeTypeArgs = HashMap<String, String>()
		val oldTypeArgs = HashMap<String, String?>()
		val hadOldTypeArg = HashSet<String>()
		for (i in tps.indices) {
			val nm = tps[i].name.asString()
			if (typeArgSubst.containsKey(nm)) {
				hadOldTypeArg.add(nm)
				oldTypeArgs[nm] = typeArgSubst[nm]
			}
			val ta = call.typeArguments.getOrNull(i)
			val bt = ta?.let { birType(it) }
			val subst = if (bt == null || bt == "gp:$nm") "object" else bt   // unresolved/self star -> object
			calleeTypeArgs[nm] = subst
			typeArgSubst[nm] = subst
			subKeys.add(nm)
		}
		fun restoreCalleeTypeArgs() {
			for (nm in subKeys) typeArgSubst[nm] = calleeTypeArgs[nm]!!
		}
		fun <T> withCallerTypeArgs(block: () -> T): T {
			for (nm in subKeys) {
				if (hadOldTypeArg.contains(nm)) typeArgSubst[nm] = oldTypeArgs[nm]!!
				else typeArgSubst.remove(nm)
			}
			return try { block() } finally { restoreCalleeTypeArgs() }
		}
		val callerTypeScope = TypeArgScope(subKeys.toList(), HashMap(oldTypeArgs), HashSet(hadOldTypeArg))
		if (extParam != null && extArg != null) {
			val tmp = "__inl${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(extParam.type))},"init":${withCallerTypeArgs { expr(extArg) }}}""")
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
				pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(p.type))},"init":${withCallerTypeArgs { expr(inlineLambdaArg ?: arg) }}}""")
				bindVal(p.name.asString(), """{"k":"local","name":${str(tmp)}}""")
			}
		}
		val result = spliceBody(bodyStatements(callee.body), callee.returnType.isUnit(), pre)
		boundVals.forEach { name -> if (hadOldVals.contains(name)) valSubst[name] = oldVals[name]!! else valSubst.remove(name) }
		boundLams.forEach { inlineLambdas.remove(it)?.let { lam -> inlineLambdaTypeScopes.remove(lam) } }
		subKeys.forEach { nm -> if (hadOldTypeArg.contains(nm)) typeArgSubst[nm] = oldTypeArgs[nm]!! else typeArgSubst.remove(nm) }
		if (boundExt) selfSubst.remove(extParam)
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	internal fun <T> withTypeArgScope(scope: TypeArgScope?, block: () -> T): T {
		if (scope == null) return block()
		val saved = HashMap<String, String?>()
		val hadSaved = HashSet<String>()
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
		return """{"k":"inlineSplice","type":${str(fileClass)},"method":${str(callee.name.asString())},"bindings":[$bindings]}"""
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
			pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(extParam.type))},"init":${expr(extArg)}}""")
			val ref = """{"k":"local","name":${str(tmp)}}"""
			selfSubst[extParam] = ref
			valSubst[extParam.name.asString()] = ref
			bound.add(extParam.name.asString())
			boundExt = true
		}
		for ((p, arg) in params.zip(regArgs)) {
			val tmp = "__lam${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(p.type))},"init":${expr(arg)}}""")
			valSubst[p.name.asString()] = """{"k":"local","name":${str(tmp)}}"""; bound.add(p.name.asString())
		}
		val result = withTypeArgScope(inlineLambdaTypeScopes[lambda]) {
			spliceBody(bodyStatements(fn.body), fn.returnType.isUnit() || call.type.isUnit(), pre)
		}
		bound.forEach { valSubst.remove(it) }
		if (boundExt) selfSubst.remove(extParam)
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	/** Emit body statements into `pre`, returning the value expression (Unit -> void const; else the last expr). */
	internal fun spliceBody(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, unit: Boolean, pre: MutableList<String>): String {
		if (unit) { stmts.forEach { pre.add(stmt(it)) }; return """{"k":"const","type":"void","value":null}""" }
		stmts.dropLast(1).forEach { pre.add(stmt(it)) }
		return when (val last = stmts.lastOrNull()) {
			is IrReturn -> expr(last.value)
			is IrExpression -> expr(last)
			else -> { last?.let { pre.add(stmt(it)) }; """{"k":"const","type":"void","value":null}""" }
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
		fun pj(name: String, t: IrType) = """{"name":${str(name)},"type":${str(birType(t))}}"""
		val capPairs = captures.map { it to captureFieldName(it) }
		// Captures arrive as leading params; rewrite body refs to those params. This must cover not only `<this>` but
		// also receiver-like captured params such as `$this$buildString`, otherwise an active inline substitution can
		// leak a caller-local (`__lam<N>`) into the lifted method body.
		capPairs.forEach { (decl, fname) -> captureSubst[decl] = """{"k":"local","name":${str(fname)}}""" }
		val capParams = capPairs.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(captureFieldType(decl))}}""" }
		val ownParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { pj(it.name.asString(), it.type) }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
		val ret = if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
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
		val elemT = call.typeArguments.getOrNull(0)?.let { birType(it) } ?: "object"
		val c = scopeCounter++
		val ptrName = "__sbp$c"; val lenName = "__sbl$c"
		val pre = arrayListOf(
			"""{"k":"var","name":${str(lenName)},"type":"int","init":${expr(args[0])}}""",
			"""{"k":"var","name":${str(ptrName)},"type":"stackptr","init":{"k":"stackAlloc","count":{"k":"local","name":${str(lenName)}},"elem":${str(elemT)}}}""")
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

	/** Inline `forEach { it -> body }` into an enumerator loop: bind `it` to a unique loop var, splice the body. */
	internal fun inlineForEach(elemT: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
		val fn = lambda.function
		val src = expr(recvExpr)
		val vname = "__fe${scopeCounter++}"
		val itParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }
		itParam?.let { valSubst[it.name.asString()] = """{"k":"local","name":${str(vname)}}""" }
		// forEach's lambda returns Unit; drop the trailing return, keep side-effect statements.
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().filter { it !is IrReturn }.joinToString(",") { stmt(it) }
		itParam?.let { valSubst.remove(it.name.asString()) }
		return """{"k":"forEachInline","elem":${str(elemT)},"src":$src,"var":${str(vname)},"body":[$body]}"""
	}

	/** First type argument's BIR type (element type of List<T>/Set<T>/etc.). */
	internal fun collectionElemType(t: IrType): String =
		(t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: "object"

	/** A lambda argument's return BIR type (for inferring LINQ result element types). */
	internal fun lambdaRet(arg: IrExpression?): String {
		val fn = (arg as? IrFunctionExpression)?.function
		return if (fn == null || fn.returnType.isUnit()) "void" else birType(fn.returnType)
	}

	/**
	 * Build a generic static call node. `shapes` names the EXACT intended overload's parameter shapes
	 * (ienum/func:N/string/gp/int/…) so ilemit picks it deterministically — no heuristic overload guessing.
	 */
	/** A `new <ExceptionType>(msg?)` node (msgJson is an already-quoted JSON string, or null for the no-arg ctor). */
	internal fun newExc(type: String, msgJson: String?): String =
		if (msgJson != null) """{"k":"clrNew","type":${str(type)},"argTypes":["System.String"],"args":[{"k":"const","type":"string","value":$msgJson}]}"""
		else """{"k":"clrNew","type":${str(type)},"argTypes":[],"args":[]}"""

	internal fun throwExpr(exc: String): String = """{"k":"throwExpr","value":$exc}"""

	internal fun clrGen(type: String, method: String, typeArgs: List<String>, shapes: List<String>, args: List<String>): String =
		"""{"k":"clrGenericStatic","type":${str(type)},"method":${str(method)},"typeArgs":[${typeArgs.joinToString(",") { str(it) }}],"shapes":[${shapes.joinToString(",") { str(it) }}],"args":[${args.joinToString(",")}]}"""

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

	/** The BIR function-type string `func:<ret>:<arg1>,<arg2>,...` for a lambda's signature (receiver first). */
	internal fun funcTypeOf(fn: IrSimpleFunction): String {
		val ps = orderedLambdaParams(fn).joinToString(",") { birTypeDeleg(it.type) }
		val ret = if (fn.returnType.isUnit()) "void" else birTypeDeleg(fn.returnType)
		return "func:$ret:$ps"
	}

	/**
	 * Like `birType`, but erases `KProperty` to `object` for delegate (Func/Action) signatures. A synthetic
	 * type (TypeBuilder) used as a generic argument to a BCL delegate triggers a Reflection.Emit limitation
	 * ("TypeBuilder generic instantiation does not support resolving members"); `Delegates.observable`'s
	 * callback takes a `KProperty` it almost always ignores, so erasing it sidesteps the issue.
	 */
	internal fun birTypeDeleg(t: IrType): String {
		val fq = t.classFqName?.asString()
		if (fq != null && (fq.startsWith("kotlin.reflect.KProperty") || fq.startsWith("kotlin.reflect.KMutableProperty"))) return "object"
		return birType(t)
	}

	/** Lambda/closure method params with KProperty erased to object (must agree with funcTypeOf for delegates):
	 *  extension receiver first (so a receiver lambda's `$this$build` is bound), then regular params. */
	internal fun lambdaParamsJson(params: List<IrValueParameter>): String =
		(params.filter { it.kind == IrParameterKind.ExtensionReceiver } + params.filter { it.kind == IrParameterKind.Regular })
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birTypeDeleg(it.type))}}""" }

	/** Regular args, filling omitted constant default arguments (IL has no default-parameter mechanism). */
	internal fun filledArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> =
		filledArgExprs(call).map { expr(it) }

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

	/**
	 * A parameter-shape token matching ilemit's `Shape(Type)` — used to pick the exact generic-method overload
	 * before `MakeGenericMethod`. A method type parameter is `gp`; primitives/strings/known generics get their
	 * canonical token; everything else is the .NET simple name (`Object`, `Int64`, ...).
	 */
	/** A parameter shape matching ilemit's `Shape()` (for resolving a generic .NET overload by name+arity+shapes). */
	internal fun clrMethodShape(t: IrType): String {
		if (t.classifierOrNull is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol) return "gp"   // bare type param
		if (isArrayType(t)) return "array"
		val fq = t.classFqName?.asString()
		when (fq) {
			"kotlin.String" -> return "string"
			"kotlin.Char" -> return "char"
			"kotlin.Int" -> return "int"
		}
		// Kotlin function types ((P..)->R / suspend (P..)->R) -> a CLR Func/Action -> ilemit "func:<#generic-args>".
		// A `(P..)->R` with R != Unit is a `Func<P..,R>` (#args = params+1); with R == Unit it is an `Action<P..>`
		// (#args = params, NO return slot) — so drop the trailing Unit from the count to match ilemit's Action shape.
		if (fq != null && (fq.startsWith("kotlin.Function") || fq.startsWith("kotlin.coroutines.SuspendFunction"))) {
			val targs = (t as? IrSimpleType)?.arguments
			val n = targs?.size ?: 1
			val retUnit = (targs?.lastOrNull() as? IrTypeProjection)?.type?.classFqName?.asString() == "kotlin.Unit"
			return "func:" + (if (retUnit && n > 0) n - 1 else n)
		}
		// Any other parameterized generic .NET type (Task<T>, Continuation<T>, …) -> "generic" (ilemit's IsGenericType default).
		if ((t as? IrSimpleType)?.arguments?.isNotEmpty() == true) return "generic"
		return netType(t).substringAfterLast('.')
	}

	internal fun extensionReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	/**
	 * Lift a function-local class to a top-level synthetic type. Referenced outer locals (incl. the enclosing
	 * `this`) become leading ctor params / capture fields; construction sites prepend those values (see the
	 * IrConstructorCall handler). Returns a no-op statement (the declaration emits nothing inline).
	 */
	internal fun liftLocalClass(klass: IrClass): String {
		if (anonNames.containsKey(klass)) return """{"k":"block","body":[]}"""   // already lifted
		val cname = "<>dotkt_${klass.name.asString()}_${scopeCounter++}"
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
			captureSubst[decl] = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		}
		liftedTypes.add(typeDef(klass, capPairs))
		capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
		localClassCaptures[klass] = captured
		return """{"k":"block","body":[]}"""
	}

	internal fun blockExpr(block: IrBlock): String {
		// `object : I { … }` -> a synthetic named class (lifted) + `new`. Instance fields are real fields;
		// captured outer values (incl. the enclosing `this`) become extra ctor params / capture fields.
		if (block.origin?.toString() == "OBJECT_LITERAL") {
			val anon = block.statements.filterIsInstance<IrClass>().firstOrNull()
			if (anon != null) {
				val cname = "<>dotkt_obj${scopeCounter++}"
				anonNames[anon] = cname
				val captured = capturedVarsForObject(anon)
				// Mutable capture (writing an outer local through the object) would need heap ref-cells.
				if (captured.any { it in mutatedIn(anon) && !isRefCell(it) })
					return unsupported(block, "an object expression that writes to a captured outer variable",
						"read-only capture works; to mutate shared state, use a small class with a field instead")
				// Capturing an ENCLOSING TYPE PARAMETER would require the synthesized object class to be generic over it
				// (reified CLR generics) AND every use of its (anonymous) type to carry the args — not yet supported, so
				// fail with a clear error instead of emitting invalid IL. (A capturing lambda or local fn does work.)
				if (freeTypeParams(captured.map { it.type }).isNotEmpty())
					return unsupported(block, "an object expression that captures an enclosing generic type parameter",
						"move the logic into a (capturing) lambda or a local fun, which do support it")
				val capPairs = captured.map { it to captureFieldName(it) }
				capPairs.forEach { (decl, fname) ->
					captureSubst[decl] = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
				}
				liftedTypes.add(typeDef(anon, capPairs))
				capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
				// Capture values are evaluated in the OUTER context (captureSubst now cleared).
				val capArgs = captured.joinToString(",") { capValueExpr(it) }
				return """{"k":"new","type":${str(cname)},"args":[$capArgs]}"""
			}
		}
		// `when (subject)` lowers to `{ val tmp = subject; WHEN }` in expression position.
		val tmp = block.statements.getOrNull(0) as? IrVariable
		val whenExpr = block.statements.getOrNull(1) as? IrWhen
		if (block.statements.size == 2 && tmp != null && whenExpr != null && tmp.initializer != null) {
			val key = tmp.name.asString()
			val origin = block.origin?.toString()
			// `a?.member` where member is a value type -> Nullable<T>: cond(a==null, default(T?), new T?(a.member)).
			if (origin == "SAFE_CALL") nullableElem(block.type)?.let { elem ->
				valSubst[key] = expr(tmp.initializer!!)
				val nullCheck = expr(whenExpr.branches.first().condition)
				val member = expr(whenExpr.branches.last().result)
				valSubst.remove(key)
				return """{"k":"cond","cond":$nullCheck,"then":{"k":"nullableNull","elem":${str(elem)}},"else":{"k":"nullableWrap","elem":${str(elem)},"e":$member}}"""
			}
			// `nv ?: d` where nv is a Nullable<T> -> evaluate once, then HasValue ? Value : d.
			if (origin == "ELVIS") nullableElem(tmp.type)?.let { elem ->
				val nv = "__nv${scopeCounter++}"
				val init = expr(tmp.initializer!!)
				// ELVIS lowers to `when { tmp == null -> fallback; else -> tmp }`:
				// branches[0].result is the fallback; branches.last() is tmp (ignored — we read .Value).
				val elseResult = expr(whenExpr.branches.first().result)
				val nvLoc = """{"k":"local","name":${str(nv)}}"""
				return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${str("nullable:$elem")},"init":$init}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${str(elem)},"e":$nvLoc},"then":{"k":"nullableValue","elem":${str(elem)},"e":$nvLoc},"else":$elseResult}}"""
			}
			valSubst[key] = expr(tmp.initializer!!)
			val result = ternary(whenExpr)
			valSubst.remove(key)
			return result
		}
		// A general block in value position: emit its preceding (side-effecting) statements, then the last value.
		// e.g. `{ counter++ }` lowers to `{ val <unary> = counter; counter = counter + 1; <unary> }` — dropping the
		// leading statements would lose the temp + the assignment.
		val last = block.statements.lastOrNull()
		if (block.statements.size > 1 && last is IrExpression) {
			val pre = block.statements.dropLast(1).joinToString(",") { stmt(it) }
			return """{"k":"valueBlock","stmts":[$pre],"result":${expr(last)}}"""
		}
		return (last as? IrExpression)?.let { expr(it) } ?: """{"k":"const","type":"void","value":null}"""
	}

	internal fun ternary(node: IrWhen): String {
		// Fold right-to-left into nested conditionals. The branches carry the when's result type, so a value-type
		// nullable result (`Int?`) gets its `T`/`null` branches coerced to Nullable<T> at emit (see EmitCond).
		val ty = str(birType(node.type))
		var acc = """{"k":"const","type":"void","value":null}"""
		for (b in node.branches.asReversed()) {
			val isElse = (b.condition as? IrConst)?.value == true
			acc = if (isElse) expr(b.result)
			else """{"k":"cond","type":$ty,"cond":${expr(b.condition)},"then":${expr(b.result)},"else":$acc}"""
		}
		return acc
	}

	/**
	 * kotlinx.atomicfu -> the DotKt.Coroutines Interlocked/Volatile wrappers: the `atomic(x)` factory -> a wrapper
	 * ctor (by arg type), and member ops (`.value`, `compareAndSet`, `incrementAndGet`, …) -> the wrapper's methods.
	 * Returns null for non-atomicfu calls. See docs §13a resolution 5.
	 */
	internal fun atomicfuCall(call: IrCall): String? {
		val callee = call.symbol.owner
		if (callee.fqNameWhenAvailable?.asString() == "kotlinx.atomicfu.atomic") {
			val arg = regularArgs(call).first()
			return when (arg.type.classFqName?.asString()) {
				"kotlin.Int" -> """{"k":"clrNew","type":"DotKtx.Atomicfu.AtomicInt","argTypes":["System.Int32"],"args":[${expr(arg)}]}"""
				"kotlin.Long" -> """{"k":"clrNew","type":"DotKtx.Atomicfu.AtomicLong","argTypes":["System.Int64"],"args":[${expr(arg)}]}"""
				"kotlin.Boolean" -> """{"k":"clrNew","type":"DotKtx.Atomicfu.AtomicBoolean","argTypes":["System.Boolean"],"args":[${expr(arg)}]}"""
				else -> {
					val typeArg = ((call.type as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type
					val elemBir = typeArg?.let { birType(it) } ?: "object"
					val elemNet = typeArg?.let { netType(it) } ?: "System.Object"   // argTypes resolve via ResolveType, not the BIR alias
					"""{"k":"clrNew","type":"clrg:DotKtx.Atomicfu.AtomicRef[$elemBir]","argTypes":[${str(elemNet)}],"args":[${expr(arg)}]}"""
				}
			}
		}
		val recv = dispatchReceiver(call) ?: return null
		val recvFq = recv.type.classFqName?.asString() ?: return null
		if (recvFq !in ATOMICFU_TYPES) return null
		val clrType = birType(recv.type)
		if (callee.correspondingPropertySymbol?.owner?.name?.asString() == "value") {
			return if (callee.name.asString().startsWith("<get"))
				"""{"k":"clrPropGet","type":${str(clrType)},"name":"Value","retType":${str(netType(call.type))},"static":false,"recv":${expr(recv)}}"""
			else {
				val v = regularArgs(call).first()
				"""{"k":"clrInstance","type":${str(clrType)},"method":"set_Value","argTypes":[${str(netType(v.type))}],"ret":"System.Void","recv":${expr(recv)},"args":[${expr(v)}]}"""
			}
		}
		val m = callee.name.asString().replaceFirstChar { it.uppercaseChar() }
		val argTs = callee.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(",") { str(netType(it.type)) }
		val argsJson = regularArgs(call).joinToString(",") { expr(it) }
		return """{"k":"clrInstance","type":${str(clrType)},"method":${str(m)},"argTypes":[$argTs],"ret":${str(netType(call.type))},"recv":${expr(recv)},"args":[$argsJson]}"""
	}

	internal fun call(call: IrCall): String {
		val callee = call.symbol.owner
		val calleeFqEarly = callee.fqNameWhenAvailable?.asString()
		// kotlin.text.MatchResult.value -> System.Text.RegularExpressions.Match.Value (find(s)?.value). Handled early
		// because it's a property getter (the generic property->field path would otherwise emit a bare-name field).
		if (callee.correspondingPropertySymbol?.owner?.name?.asString() == "value" &&
			dispatchReceiver(call)?.type?.classFqName?.asString() == "kotlin.text.MatchResult")
			return """{"k":"clrPropGet","type":"System.Text.RegularExpressions.Match","name":"Value","retType":"System.String","static":false,"recv":${expr(dispatchReceiver(call)!!)}}"""
		// `.message`/`.cause` on a Throwable subclass (incl. a user `class E : Exception`) -> System.Exception
		// .Message/.InnerException. Handled early because for a user subclass the getter resolves to a fake-override
		// whose owner is the user class, so the generic property->field path would emit a bare (missing) field.
		callee.correspondingPropertySymbol?.owner?.name?.asString()?.let { pn ->
			if ((pn == "message" || pn == "cause") && isThrowableType(dispatchReceiver(call)?.type)) {
				val (prop, rt) = if (pn == "message") "Message" to "System.String" else "InnerException" to "System.Exception"
				return """{"k":"clrPropGet","type":"System.Exception","name":${str(prop)},"retType":${str(rt)},"static":false,"recv":${expr(dispatchReceiver(call)!!)}}"""
			}
		}
		// `Result.success(v)` / `Result.failure(e)` as a VALUE -> DotKt.Result.Success/Failure (so a Result
		// can be constructed and forwarded anywhere, e.g. into a user `Continuation.resumeWith`). T4 / docs §13n.
		if (calleeFqEarly == "kotlin.Result.Companion.success" || calleeFqEarly == "kotlin.Result.Companion.failure") {
			val t = firstArgBir(call.type)   // Unit-aware (Result<Unit> -> DotKt.Unit, not void)
			val spec = "clrg:DotKt.Result[$t]"
			return if (calleeFqEarly.endsWith("success"))
				"""{"k":"clrStatic","type":${str(spec)},"method":"Success","argTypes":[${str(t)}],"ret":${str(spec)},"args":[${expr(regularArgs(call).first())}]}"""
			else
				"""{"k":"clrStatic","type":${str(spec)},"method":"Failure","argTypes":["clr:System.Exception"],"ret":${str(spec)},"args":[${expr(regularArgs(call).first())}]}"""
		}
		atomicfuCall(call)?.let { return it }
		// `kotlin.sequences.sequence { yield(…) }` -> a lazy IEnumerable<T> backed by a yield state machine that
		// implements ISeqStep<T>, wrapped by DotKt.Sequences.Seq.Of. The block's yields CPS-linearize to coYield
		// steps (multi-shot). See docs §13h. v1: the block must not capture outer state (loud error otherwise).
		if (calleeFqEarly == "kotlin.sequences.sequence") {
			val block = regularArgs(call).firstOrNull() as? IrFunctionExpression
				?: return unsupported(call, "this sequence{} block", "expected a literal lambda")
			if (capturedVars(block.function, includeThis = true).isNotEmpty())
				return unsupported(call, "a capturing sequence{} block", "v1 supports only non-capturing sequence builders")
			val elem = ((call.type as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { birType(it) } ?: "object"
			val co = emitCoroutineBody(block.function)   // yields -> coYield steps; live locals -> cpsFields
			val smName = "<>dotkt_${synthScope}_Seq${closureCounter++}"
			return """{"k":"sequenceNew","sm":${str(smName)},"elem":${str(elem)},"cpsFields":[${co.cpsFields}],"steps":[${co.steps}]}"""
		}
		// `generateSequence(seed?, next)` / `generateSequence(next)` -> a lazy IEnumerable<T> from Seq.Generate*.
		// Pick the value- vs reference-T variant at compile time (the `(T)->T?` delegate shape differs); see §13u.
		if (calleeFqEarly == "kotlin.sequences.generateSequence") {
			val args = regularArgs(call)
			val elem = call.typeArguments.firstOrNull()?.let { birType(it) } ?: "object"
			// Value-type T -> the GenerateVal variant (next is Func<T, Nullable<T>>); reference T -> GenerateRef.
			val isVal = elem in setOf("int", "long", "short", "byte", "bool", "char", "double", "float")
			return if (args.size == 2) {
				val method = if (isVal) "GenerateVal" else "GenerateRef"
				val seedShape = if (isVal) "generic" else "gp"   // value seed is Nullable<T> (generic), ref seed is bare T
				"""{"k":"clrGenericStatic","type":"DotKt.Sequences.Seq","method":${str(method)},"typeArgs":[${str(elem)}],"shapes":["$seedShape","func:2"],"args":[${expr(args[0])},${expr(args[1])}]}"""
			} else {
				val method = if (isVal) "GenerateValN" else "GenerateRefN"
				"""{"k":"clrGenericStatic","type":"DotKt.Sequences.Seq","method":${str(method)},"typeArgs":[${str(elem)}],"shapes":["func:1"],"args":[${expr(args[0])}]}"""
			}
		}
		// `kotlinx.coroutines.runBlocking { … }` -> drive the coroutine synchronously. Only a TRIVIAL block
		// (`{ suspendFun() }`, a single tail suspend call) is supported here (a non-trivial block needs suspend-lambda
		// CPS). The block's kickoff Task is awaited via GetAwaiter().GetResult().
		if (callee.fqNameWhenAvailable?.asString() == "kotlinx.coroutines.runBlocking") {
			val block = regularArgs(call).lastOrNull() as? IrFunctionExpression
			val stmts = (block?.function?.body as? IrBlockBody)?.statements.orEmpty()
			val tail = stmts.singleOrNull()?.let { if (it is IrReturn) it.value else it as? IrExpression }
			if (block != null && tail is IrCall && tail.symbol.owner.isSuspend) {
				val unit = call.type.isUnit()
				val taskT = "clrg:System.Threading.Tasks.Task" + (if (unit) "" else "[${birType(tail.type)}]")
				val awaiterT = "clrg:System.Runtime.CompilerServices.TaskAwaiter" + (if (unit) "" else "[${birType(tail.type)}]")
				val getAwaiter = """{"k":"clrInstance","type":${str(taskT)},"method":"GetAwaiter","argTypes":[],"ret":${str(awaiterT)},"recv":${expr(tail)},"args":[]}"""
				return """{"k":"clrInstance","type":${str(awaiterT)},"method":"GetResult","argTypes":[],"ret":${str(if (unit) "System.Void" else netType(tail.type))},"recv":$getAwaiter,"args":[]}"""
			}
			return unsupported(call, "this runBlocking block",
				"only a trivial block `{ suspendFun() }` is supported; extract the body into a `suspend fun` and call that")
		}
		// `stackBuffer(n) { … }` intrinsic -> scoped stack allocation (splice the block into the caller's frame).
		if (callee.name.asString() == "stackBuffer" && callee.parent is org.jetbrains.kotlin.ir.declarations.IrPackageFragment)
			return emitStackBuffer(call)
		// A `StackBuffer<T>` member access while its block is being spliced -> a stack op (ptr + index).
		((dispatchReceiver(call) as? IrGetValue)?.symbol?.owner)?.let { stackBufSubst[it] }?.let { return emitStackBufferOp(call, callee, it) }
		// A `<get-x>`/`<set-x>` call for a LOCAL delegated property -> access on the delegate local (thisRef=null,
		// no enclosing instance). `by lazy`: the local's `.Value`; custom delegate: getValue/setValue(null, KProperty).
		localDelegates[callee]?.let { ldp ->
			val dvar = ldp.delegate
			val dlocal = """{"k":"local","name":${str(dvar.name.asString())}}"""
			val elem = birType(ldp.getter.returnType)
			// A `ClrRef<T>` delegate (byref local): getValue/setValue inline to ldobj/stobj through the managed pointer.
			if (birType(dvar.type).startsWith("byref:"))
				return if (callee === ldp.setter)
					"""{"k":"byrefStore","local":${str(dvar.name.asString())},"elem":${str(elem)},"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"byrefLoad","local":${str(dvar.name.asString())},"elem":${str(elem)}}"""
			if (dvar.type.classFqName?.asString() == "kotlin.Lazy" && callee === ldp.getter)
				return """{"k":"clrPropGet","type":${str("clrg:System.Lazy[$elem]")},"name":"Value","retType":${str(elem)},"static":false,"recv":$dlocal}"""
			val delegateClass = dvar.type.classifierOrNull?.owner as? IrClass
			val ownerName = when {
				delegateClass != null && clrName(delegateClass) == null &&
					delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true -> typeName(delegateClass)
				else -> propIface(dvar.type)
			}
			if (ownerName != null) {
				needsKProperty = true
				val kprop = """{"k":"new","type":"<>dotkt_KPropertyImpl","args":[{"k":"const","type":"string","value":${str(ldp.name.asString())}}]}"""
				val nullRef = """{"k":"const","type":"void","value":null}"""
				return if (callee === ldp.setter)
					"""{"k":"callInstance","ownerType":${str(ownerName)},"virtual":true,"recv":$dlocal,"method":"setValue","args":[$nullRef,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":${str(ownerName)},"virtual":true,"recv":$dlocal,"method":"getValue","args":[$nullRef,$kprop]}"""
			}
		}
		val name = callee.name.asString()
		val declaringClass = callee.parent as? IrClass
		// A top-level fn has no declaringClass; fall back to the callee's OWN package so an injected/user top-level
		// operator (e.g. a restored `operator fun Vec.plus`) isn't mistaken for a kotlin builtin and lowered to a `bin`.
		val isBuiltin = (declaringClass?.fqNameWhenAvailable?.asString() ?: callee.fqNameWhenAvailable?.asString())?.startsWith("kotlin") ?: true
		val pkgFqName = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
		val calleeFq = if (declaringClass == null && pkgFqName != null) "$pkgFqName.$name" else null

		// A call to a lifted local function -> static call with captured values (incl. enclosing `this`) prepended.
		localFns[callee]?.let { (lname, caps, tps) ->
			val capArgs = caps.map { capValueExpr(it) }
			// If the lifted method is generic (captured enclosing type params), pass them as type arguments.
			val typeArgs = if (tps.isEmpty()) "" else ""","typeArgs":[${tps.joinToString(",") { str("gp:" + it.name.asString()) }}]"""
			return """{"k":"callStatic","owner":null,"method":${str(lname)},"args":[${(capArgs + filledArgs(call)).joinToString(",")}]$typeArgs}"""
		}

		// Inlining (lambda-param inline funs only; lambda-less inline = JIT's job — see [[clr-not-jvm-discard-jvmisms]]).
		// (1) An invoke on an inlined lambda param (`action(x)` inside the spliced inline-fun body) -> splice the
		//     lambda body in place. A non-local `return` inside it targets the enclosing fun (already so in the IR),
		//     and valueBlock is emitted INLINE (not an IIFE), so it returns from the caller = correct non-local return.
		if (name == "invoke") (dispatchReceiver(call) as? IrGetValue)?.symbol?.owner?.let { recv ->
			inlineLambdas[recv]?.let { return spliceLambdaCall(it, call) }
		}
		// (2) A call to a USER `inline fun` that takes a lambda arg -> splice its body (real inlining). stdlib inline
		//     bodies are absent from our IR, so only user inline funs (body present) inline; others fall through.
		if (callee.isInline && callee.body != null && hasLambdaArg(call)) return inlineCall(call)

		// `Delegates.observable/vetoable/notNull(…)` -> a `new <synthesized delegate>(args)` (stdlib bodies are
		// absent from our IR, so we compiler-generate the equivalent delegate class, monomorphized by value type).
		if (declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.properties.Delegates" &&
			(name == "observable" || name == "vetoable" || name == "notNull")) {
			val v = (call.type as? IrSimpleType)?.arguments?.getOrNull(1)
				?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			val cname = synthDelegate(name, v)
			return """{"k":"new","type":${str(cname)},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
		}
		// `by lazy { … }` -> the delegate is `new System.Lazy<T>(Func<T>)` (initializer is the last arg in every
		// `lazy` overload; any thread-safety mode is dropped — System.Lazy defaults to synchronized, as Kotlin does).
		if (calleeFq == "kotlin.lazy") {
			val elem = (call.type as? IrSimpleType)?.arguments?.firstOrNull()
				?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			val init = regularArgs(call).lastOrNull()?.let { expr(it) } ?: """{"k":"const","type":"void","value":null}"""
			return """{"k":"clrNew","type":${str("clrg:System.Lazy[$elem]")},"argTypes":[${str("func:$elem:")}],"args":[$init]}"""
		}

		if (name == "compareTo") {
			val recv = dispatchReceiver(call)
			val arg = regularArgs(call).firstOrNull()
			val ec = recv?.type?.classifierOrNull?.owner as? IrClass
			if (recv != null && arg != null && ec?.kind == ClassKind.ENUM_CLASS) {
				fun ord(e: IrExpression): String = if (isRichEnum(ec))
					"""{"k":"field","ownerType":${str(typeName(ec))},"recv":${expr(e)},"name":"__ordinal"}"""
				else """{"k":"enumOrdinal","e":${expr(e)}}"""
				return """{"k":"bin","op":"-","l":${ord(recv)},"r":${ord(arg)}}"""
			}
		}

		// `a.compareTo(b)` (the desugaring of `<`/`>`/`<=`/`>=` on a Comparable, incl. a bounded generic param
		// `<T : Comparable<T>>`) -> a `constrained.` callvirt to `System.IComparable<T>::CompareTo`. The
		// `constrained.` prefix dispatches uniformly whether the receiver is a value type, a reference type, or
		// an open type parameter, so this one shape covers `Int`/`String`/`T`.
		if (declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.Comparable" && name == "compareTo") {
			val recv = dispatchReceiver(call)!!
			val rt = birType(recv.type)
			return """{"k":"constrainedCall","recvType":${str(rt)},"iface":${str("clrg:System.IComparable[$rt]")},"method":"CompareTo","recv":${expr(recv)},"arg":${expr(regularArgs(call).first())}}"""
		}

		// NOTE: `reified` gets NO special handling here — it is deliberately never inspected. The CLR has reified
		// generics, so `reified` is pure decoration: a generic function (reified or not) is just emitted as a .NET
		// generic method, and a body that uses `T::class`/`x is T`/`x as T` lowers to `ldtoken !!0`/`isinst !!0`
		// like any other generic-method body. (On the JVM `reified` exists ONLY to drive call-site inlining around
		// erasure; that whole machine is absent here.) See [[clr-not-jvm-discard-jvmisms]].

		// `T::class.simpleName`/`.qualifiedName` (KClass over a System.Type) -> Type.Name/FullName.
		if (declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.reflect.KClass") {
			val recv = dispatchReceiver(call)
			val m = when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
				"simpleName" -> "get_Name"; "qualifiedName" -> "get_FullName"; else -> null
			}
			if (recv != null && m != null)
				return """{"k":"clrInstance","type":"System.Type","method":${str(m)},"argTypes":[],"ret":"System.String","recv":${expr(recv)},"args":[]}"""
		}

		// `runCatching { block }` -> a value-block: `var r; try { r = Result(block(), null, true) } catch(e) { r =
		// Result(default, e, false) }; r`. Result is the synthetic generic type; the block is spliced inline.
		if ((calleeFq == "kotlin.runCatching" || name == "runCatching") && call.type.classFqName?.asString() == "kotlin.Result") {
			(regularArgs(call).getOrNull(0) as? IrFunctionExpression)?.let { lam ->
				var elem = (call.type as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
				if (elem == "void") elem = "object"
				val spec = "clrg:DotKt.Result[$elem]"
				val rcVar = "__rc${scopeCounter++}"
				val rcLoc = """{"k":"local","name":${str(rcVar)}}"""
				val pre = ArrayList<String>()
				val unit = lam.function.returnType.isUnit()
				val v = if (unit) { spliceBody(bodyStatements(lam.function.body), true, pre); """{"k":"const","type":"object","value":null}""" }
					else spliceBody(bodyStatements(lam.function.body), false, pre)
				// ok -> Result.Success(value); err -> Result.Failure(e). (Unit block -> Success(null-as-object).)
				val mkOk = """{"k":"clrStatic","type":${str(spec)},"method":"Success","argTypes":[${str(elem)}],"ret":${str(spec)},"args":[$v]}"""
				val mkErr = """{"k":"clrStatic","type":${str(spec)},"method":"Failure","argTypes":["clr:System.Exception"],"ret":${str(spec)},"args":[{"k":"local","name":"e"}]}"""
				val tryBody = (pre + """{"k":"setLocal","name":${str(rcVar)},"value":$mkOk}""").joinToString(",")
				val tryN = """{"k":"try","type":"void","body":[$tryBody],"catches":[{"excType":"System.Exception","var":"e","body":[{"k":"setLocal","name":${str(rcVar)},"value":$mkErr}]}]}"""
				val decl = """{"k":"var","name":${str(rcVar)},"type":${str(spec)},"init":null}"""
				return """{"k":"valueBlock","stmts":[$decl,$tryN],"result":$rcLoc}"""
			}
		}
		// Result method-accessors -> inline over the DotKt.Result struct's properties (IsSuccess/Value/
		// ExceptionOrNull). (getOrNull/getOrThrow/exceptionOrNull are members; getOrDefault is an extension.) The
		// property getters isSuccess/isFailure arrive instead as IrGetField (inline value class) — see expr().
		if ((dispatchReceiver(call) ?: extensionReceiver(call))?.type?.classFqName?.asString() == "kotlin.Result" &&
			name in setOf("getOrNull", "getOrThrow", "getOrDefault", "exceptionOrNull", "isFailure", "isSuccess")) {
			val r = dispatchReceiver(call) ?: extensionReceiver(call)!!
			val spec = birType(r.type)
			val rv = expr(r)
			fun prop(n: String, rt: String) = """{"k":"clrPropGet","type":${str(spec)},"name":${str(n)},"retType":${str(rt)},"static":false,"recv":$rv}"""
			val succ = prop("IsSuccess", "bool"); val value = prop("Value", birType((r.type as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type } ?: r.type)); val fail = prop("ExceptionOrNull", "clr:System.Exception")
			return when (name) {
				"isSuccess" -> succ
				"isFailure" -> """{"k":"un","op":"!","e":$succ}"""
				"exceptionOrNull" -> fail
				"getOrDefault" -> """{"k":"cond","cond":$succ,"then":$value,"else":${expr(regularArgs(call).first())}}"""
				"getOrThrow" -> """{"k":"cond","cond":$succ,"then":$value,"else":${throwExpr(fail)}}"""
				else -> {  // getOrNull(): T? — value T -> Nullable<T> (both branches), ref T -> value-or-null.
					// Use call.type (the SUBSTITUTED Int?/String?), not callee.returnType (the generic T?).
					val rt = birType(call.type)
					if (rt.startsWith("nullable:")) {
						val ve = rt.removePrefix("nullable:")
						"""{"k":"cond","cond":$succ,"then":{"k":"nullableOf","elem":${str(ve)},"e":$value},"else":{"k":"default","type":${str(rt)}}}"""
					} else """{"k":"cond","cond":$succ,"then":$value,"else":{"k":"const","type":${str(rt)},"value":null}}"""
				}
			}
		}

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

		// Collection factories `listOf`/`setOf` -> a `listNew`/`setNew` (List<elem>/HashSet<elem>). Handles both the
		// vararg overload (`listOf(a, b, …)`) and the single-element overload (`listOf(x)` is NOT a vararg). The
		// element type comes from the call's `List<T>` return so a single-element `listOf(3)` is List<Int>, not <object>.
		if (calleeFq in LIST_FACTORIES || calleeFq in SET_FACTORIES) {
			val elems = (call.arguments.firstOrNull() as? IrVararg)?.elements?.filterIsInstance<IrExpression>()
				?: regularArgs(call)
			val elemT = collectionElemType(call.type)
			val kind = if (calleeFq in SET_FACTORIES) "setNew" else "listNew"
			return """{"k":${str(kind)},"elem":${str(elemT)},"elems":[${elems.joinToString(",") { expr(it) }}]}"""
		}
		// `mapOf(k to v, …)` -> a Dictionary<K,V> (each element is a `to` call: key=ext recv, value=arg).
		if (calleeFq in MAP_FACTORIES) {
			val (kt, vt) = mapKV(call.type)
			val pairs = (call.arguments.firstOrNull() as? IrVararg)?.elements?.filterIsInstance<IrCall>().orEmpty()
			val entries = pairs.joinToString(",") { p -> """{"key":${expr(extensionReceiver(p)!!)},"val":${expr(regularArgs(p).first())}}""" }
			return """{"k":"mapNew","keyType":${str(kt)},"valType":${str(vt)},"entries":[$entries]}"""
		}

		// Array factory `intArrayOf(...)`/`arrayOf(...)` -> a `newArray` (vararg elements).
		if (declaringClass == null && name in ARRAY_FACTORY_NAMES &&
			(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString() == "kotlin") {
			val v = call.arguments.firstOrNull() as? IrVararg
			val elems = v?.elements?.filterIsInstance<IrExpression>().orEmpty()
			// Prefer the generic `arrayOf<T>`'s type argument (reliable even when EMPTY); fall back to the vararg's
			// element type (for the non-generic primitive factories like intArrayOf).
			val elemT = call.typeArguments.getOrNull(0)?.let { birType(it) } ?: v?.let { birType(it.varargElementType) } ?: "object"
			return """{"k":"newArray","elem":${str(elemT)},"elems":[${elems.joinToString(",") { expr(it) }}]}"""
		}
		// `e!!` (not-null assertion) -> the value itself (the use site throws on null anyway).
		if (name == "CHECK_NOT_NULL") return expr(call.arguments.filterNotNull().first())

		// Primitive range operators are declared as Kotlin builtins, but CLR primitives have no instance methods.
		// Materialize the stdlib range classes directly for value-position ranges; structured for-loops are handled
		// in birForLoop.
		if (name == "rangeTo" || name == "rangeUntil") {
			val cls = declaringClass?.fqNameWhenAvailable?.asString()
			val recv = dispatchReceiver(call)
			val end = regularArgs(call).firstOrNull()
			if (recv != null && end != null) {
				val rangeType = when (cls) {
					"kotlin.Byte", "kotlin.Short", "kotlin.Int" -> "kotlin.ranges.IntRange"
					"kotlin.Long" -> "kotlin.ranges.LongRange"
					"kotlin.Char" -> "kotlin.ranges.CharRange"
					else -> null
				}
				if (rangeType != null) {
					val endExpr = if (name == "rangeUntil") {
						val one = if (cls == "kotlin.Long") """{"k":"const","type":"long","value":1}""" else """{"k":"const","type":"int","value":1}"""
						"""{"k":"bin","op":"-","l":${expr(end)},"r":$one}"""
					} else expr(end)
					return """{"k":"new","type":${str(rangeType)},"args":[${expr(recv)},$endExpr]}"""
				}
			}
		}

		// `x in a..b` (range membership) -> `(x >= a && x <op> b)` via a short-circuit cond.
		if (name == "contains") {
			val range = dispatchReceiver(call) as? IrCall
			val value = regularArgs(call).firstOrNull()
			if (range != null && value != null) {
				val ops = range.arguments.filterNotNull()
				val cmp = when (range.symbol.owner.name.asString()) { "rangeTo" -> "<="; "until", "rangeUntil" -> "<"; else -> null }
				if (cmp != null && ops.size == 2) {
					val x = expr(value); val lo = expr(ops[0]); val hi = expr(ops[1])
					return """{"k":"cond","cond":{"k":"bin","op":">=","l":$x,"r":$lo},"then":{"k":"bin","op":${str(cmp)},"l":$x,"r":$hi},"else":{"k":"const","type":"bool","value":false}}"""
				}
			}
		}

		// Enum rich API: Color.values()/entries -> Enum.GetValues<T>(); Color.valueOf(s) -> Enum.Parse<T>(s).
		(callee.parent as? IrClass)?.takeIf { it.kind == ClassKind.ENUM_CLASS }?.let { ec ->
			val et = "@" + ec.name.asString()
			// Rich enum -> the synthesized static values()/valueOf() methods on the class.
			if (isRichEnum(ec)) {
				if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
					return """{"k":"callStatic","owner":${str(ec.name.asString())},"method":"values","args":[]}"""
				if (name == "valueOf") return """{"k":"callStatic","owner":${str(ec.name.asString())},"method":"valueOf","args":[${expr(regularArgs(call).first())}]}"""
			}
			if (name == "values" || callee.correspondingPropertySymbol?.owner?.name?.asString() == "entries")
				return """{"k":"enumValues","type":${str(et)}}"""
			if (name == "valueOf") return """{"k":"enumParse","type":${str(et)},"arg":${expr(regularArgs(call).first())}}"""
		}
		// `c.code` (Char -> Int code point) -> the char value as an int.
		if (callee.correspondingPropertySymbol?.owner?.name?.asString() == "code")
			(dispatchReceiver(call) ?: extensionReceiver(call))?.takeIf { it.type.classFqName?.asString() == "kotlin.Char" }?.let { c ->
				return """{"k":"conv","to":"int","e":${expr(c)}}"""
			}
		// c.name -> ToString() (enum name); c.ordinal -> (int)c.  Rich enum -> the __name/__ordinal fields.
		dispatchReceiver(call)?.takeIf { (it.type.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS }?.let { rc ->
			val rec = (rc.type.classifierOrNull?.owner as? IrClass)
			if (rec != null && isRichEnum(rec)) when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
				"name" -> return """{"k":"field","ownerType":${str(rec.name.asString())},"recv":${expr(rc)},"name":"__name"}"""
				"ordinal" -> return """{"k":"field","ownerType":${str(rec.name.asString())},"recv":${expr(rc)},"name":"__ordinal"}"""
			}
			when (callee.correspondingPropertySymbol?.owner?.name?.asString()) {
				"name" -> return """{"k":"objMethod","method":"ToString","recv":${expr(rc)}}"""
				"ordinal" -> return """{"k":"enumOrdinal","e":${expr(rc)}}"""
			}
		}

		// `a to b` -> the stdlib Pair class. Pair/Triple/IndexedValue are emitted by stdlib itself, so don't
		// lower their ABI to CLR ValueTuple here.
		if (calleeFq == "kotlin.to") {
			val a = extensionReceiver(call); val b = regularArgs(call).getOrNull(0)
			if (a != null && b != null)
				return """{"k":"new","type":"kotlin.Pair[${birType(a.type)},${birType(b.type)}]","args":[${expr(a)},${expr(b)}]}"""
		}
		if (declaringClass?.fqNameWhenAvailable?.asString() in setOf("kotlin.Pair", "kotlin.Triple", "kotlin.collections.IndexedValue")
			&& name.startsWith("component") && name.drop("component".length).all { it.isDigit() }) {
			dispatchReceiver(call)?.let { r ->
				val field = when (declaringClass?.fqNameWhenAvailable?.asString() to name) {
					"kotlin.Pair" to "component1", "kotlin.Triple" to "component1", "kotlin.collections.IndexedValue" to "component1" -> if (declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.collections.IndexedValue") "index" else "first"
					"kotlin.Pair" to "component2", "kotlin.Triple" to "component2", "kotlin.collections.IndexedValue" to "component2" -> if (declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.collections.IndexedValue") "value" else "second"
					"kotlin.Triple" to "component3" -> "third"
					else -> null
				}
				if (field != null) return """{"k":"field","ownerType":${str(birType(r.type).removePrefix("@"))},"recv":${expr(r)},"name":${str(field)}}"""
			}
		}
		// `entry.component1()/.component2()` on a Map.Entry (the `for ((k,v) in map)` desugaring; an EXTENSION
		// function, so the receiver is the extension receiver) -> KeyValuePair.Key/.Value.
		if (name == "component1" || name == "component2") {
			val r = dispatchReceiver(call) ?: extensionReceiver(call)
			if (r != null && r.type.classFqName?.asString() in setOf("kotlin.collections.Map.Entry", "kotlin.collections.MutableMap.MutableEntry")) {
				val a = (r.type as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
				val kt = a.getOrNull(0) ?: "object"; val vt = a.getOrNull(1) ?: "object"
				val prop = if (name == "component1") "Key" else "Value"
				return """{"k":"clrPropGet","type":"clrg:System.Collections.Generic.KeyValuePair[$kt,$vt]","name":${str(prop)},"retType":${str(if (name == "component1") kt else vt)},"static":false,"recv":${expr(r)}}"""
			}
		}

		// Invoking a function-typed value `f(x)` -> delegate `Invoke` (Func/Action). Includes a callable-reference
		// value `(c::method)(x)` whose static type is `KFunctionN` (also a delegate at the CLR level).
		if (name == "invoke" && declaringClass?.fqNameWhenAvailable?.asString().let { it?.startsWith("kotlin.Function") == true || it?.startsWith("kotlin.reflect.KFunction") == true }) {
			val recv = dispatchReceiver(call)
			if (recv != null) {
				val a = regularArgs(call)
				return """{"k":"delegateInvoke","funcType":${str(birType(recv.type))},"recv":${expr(recv)},"args":[${a.joinToString(",") { expr(it) }}]}"""
			}
		}
		// MutableList/MutableCollection mutation members (`add`/`remove`/`clear`/`removeAt`) -> the BCL List<T>
		// instance method. Kotlin collections lower to System.Collections.Generic.List<T>; these are instance calls,
		// not COLLECTION_OPS extension ops (those already returned above). Lets the real stdlib `map`/`filter`/`mapTo`
		// — which build an ArrayList via `.add(...)` — run on the BCL list. `contains`/`indexOf` stay COLLECTION_OPS.
		// Array indexing `a[i]` / `a[i] = v` (the `get`/`set` operators on Array/primitive arrays).
		if (callee.isOperator && (name == "get" || name == "set")) {
			val recv = dispatchReceiver(call)
			if (recv != null && isArrayType(recv.type)) {
				val elemT = arrayElemType(recv.type); val a = regularArgs(call)
				return if (name == "get") """{"k":"arrayGet","elem":${str(elemT)},"array":${expr(recv)},"index":${expr(a[0])}}"""
				else """{"k":"arraySet","elem":${str(elemT)},"array":${expr(recv)},"index":${expr(a[0])},"value":${expr(a[1])}}"""
			}
			// String indexing `s[i]` -> System.String.get_Chars(i) (char).
			if (recv != null && name == "get" && recv.type.classFqName?.asString() == "kotlin.String")
				return """{"k":"clrInstance","type":"System.String","method":"get_Chars","argTypes":["System.Int32"],"ret":"System.Char","recv":${expr(recv)},"args":[${expr(regularArgs(call)[0])}]}"""
			// kotlin.* List/Map indexing `list[i]`/`m[k]` is NOT intercepted: in FIR it's already an operator call to
			// `get`/`set` — fall through to the ordinary call path so it emits as a real kotlin.* `get`/`set` call.
			// Injected .NET indexer `c[i]` / `c[i] = v` -> get_Item / set_Item on the constructed .NET type. The
			// receiver's type carries the element type arg (`Collection<Int>`), so the constructed `clrg:...[int]`
			// resolves the substituted accessor.
			val ixOwner = (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass) ?: declaringClass
			if (recv != null && ixOwner != null && clrName(ixOwner) != null) {
				val mt = birType(recv.type); val a = regularArgs(call)
				return if (name == "get")
					"""{"k":"clrInstance","type":${str(mt)},"method":"get_Item","argTypes":[${str(netType(a[0].type))}],"ret":${str(netType(call.type))},"recv":${expr(recv)},"args":[${expr(a[0])}]}"""
				else
					"""{"k":"clrInstance","type":${str(mt)},"method":"set_Item","argTypes":[${str(netType(a[0].type))},${str(netType(a[1].type))}],"ret":"System.Void","recv":${expr(recv)},"args":[${expr(a[0])},${expr(a[1])}]}"""
			}
		}

		// BCL interop: a call whose declaring class is a .NET type (`@Clr` or injected) resolves to a real .NET
		// member. An INHERITED .NET member (e.g. `appError.Message`) is a fake-override whose `parent` is the
		// Kotlin subclass, so resolve through the fake override to find the real .NET declaring type.
		val clrType = declaringClass?.let { clrName(it) }
			?: (callee.takeIf { it.isFakeOverride }?.resolveFakeOverride()?.parent as? IrClass)?.let { clrName(it) }
			// A synthesized companion of an injected .NET type holds its STATIC members (`App.Start`) -> a static call
			// on the .NET type itself.
			?: declaringClass?.takeIf { it.isCompanion }?.let { it.parent as? IrClass }?.let { clrName(it) }
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
				else -> recvClass?.superTypes?.firstOrNull { it.classifierOrNull?.owner == declClass }?.let { birType(it) } ?: clrType
			}
			// I4: an injected `add_<E>`/`remove_<E>` call is a .NET event subscription -> `recv.<E> += handler`.
			// The real .NET declaring type owns the event; the FIR injector recorded (eventName, op) for the
			// synthesized accessor. The handler is a lambda -> delegate (the existing closureNew/delegateNew path);
			// ilemit binds it to the event's own delegate type (not Func/Action). See clrEventAdd in ilemit.
			val declFq = declClass?.fqNameWhenAvailable?.asString()
			kotc.ClrEventRegistry.lookup(declFq, name)?.let { (eventName, op) ->
				val recvJson = if (isStatic) "null" else expr(recv!!)
				val handler = expr(regularArgs(call).first())
				val kind = if (op == "+=") "clrEventAdd" else "clrEventRemove"
				return """{"k":${str(kind)},"type":${str(memberType)},"event":${str(eventName)},"static":$isStatic,"recv":$recvJson,"handler":$handler}"""
			}
			// A generic .NET method (`Unsafe.SizeOf<T>()`, `Activator.CreateInstance<T>()`) -> resolve the open
			// generic-method definition by name + type-arity + parameter shapes, then MakeGenericMethod with the
			// call's type args. The CLR has reified generics, so this is just an ordinary generic-method call (no
			// erasure dance) — see [[clr-not-jvm-discard-jvmisms]]. Static -> clrGenericStatic, instance -> ...Instance.
			if (callee.typeParameters.isNotEmpty()) {
				val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
				if (targs.all { it != null }) {
					val taJson = targs.joinToString(",") { str(birType(it!!)) }
					val member = clrName(callee) ?: name
					// A generic MEMBER extension (`class C { fun <R> T.f() }`): the `__self` receiver is the .NET method's
					// first param -> prepend its value + shape so by-shape overload resolution and the call line up.
					val gExt = if (!isStatic) extensionReceiver(call) else null
					val shapeParams = (if (gExt != null) listOf(gExt.type) else emptyList()) + regularParams(callee).map { it.type }
					val shapes = shapeParams.joinToString(",") { str(clrMethodShape(it)) }
					val argsJson = (listOfNotNull(gExt) + regularArgs(call)).joinToString(",") { expr(it) }
					return if (isStatic)
						"""{"k":"clrGenericStatic","type":${str(clrType)},"method":${str(member)},"typeArgs":[$taJson],"shapes":[$shapes],"args":[$argsJson]}"""
					else
						"""{"k":"clrGenericInstance","type":${str(memberType)},"method":${str(member)},"typeArgs":[$taJson],"shapes":[$shapes],"recv":${expr(recv!!)},"args":[$argsJson]}"""
				}
			}
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) {
				val pn = clrName(prop) ?: prop.name.asString()
				val recvJson = if (isStatic) "null" else expr(recv!!)
				// A restored MEMBER extension property (`class C { val T.p }`): no .NET property exists — it's a
				// `get_p(__self)`/`set_p(__self, v)` method on the dispatch type, the extension receiver as `__self`.
				extensionReceiver(call)?.let { pExt ->
					return if (callee === prop.setter)
						"""{"k":"clrInstance","type":${str(memberType)},"method":${str("set_$pn")},"argTypes":[${str(netType(pExt.type))},${str(netType(regularArgs(call).first().type))}],"ret":"System.Void","recv":$recvJson,"args":[${expr(pExt)},${expr(regularArgs(call).first())}]}"""
					else """{"k":"clrInstance","type":${str(memberType)},"method":${str("get_$pn")},"argTypes":[${str(netType(pExt.type))}],"ret":${str(netType(callee.returnType))},"recv":$recvJson,"args":[${expr(pExt)}]}"""
				}
				return if (callee === prop.setter)
					"""{"k":"clrPropSet","type":${str(memberType)},"name":${str(pn)},"static":$isStatic,"recv":$recvJson,"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"clrPropGet","type":${str(memberType)},"name":${str(pn)},"retType":${str(netType(callee.returnType))},"static":$isStatic,"recv":$recvJson}"""
			}
			val member = clrName(callee) ?: name
			val argsJson = regularArgs(call).joinToString(",") { expr(it) }
			// A restored `suspend` member's .NET method returns Task<T> (awaited via the coroutine machinery), not T.
			val ret = str(if (callee.isSuspend) coTaskType(call.type) else netType(callee.returnType))
			// A .NET operator/conversion (`op_Addition`/`op_Equality`/`op_Implicit`…) is a STATIC method; a Kotlin
			// `operator fun` models it as an instance member, so prepend the receiver as the first argument.
			if (member.startsWith("op_") && !isStatic && recv != null) {
				val allArgs = (listOf(expr(recv)) + regularArgs(call).map { expr(it) }).joinToString(",")
				val allArgTypes = (listOf(str(netType(recv.type))) + regularArgs(call).map { str(netType(it.type)) }).joinToString(",")
				return """{"k":"clrStatic","type":${str(memberType)},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"args":[$allArgs]}"""
			}
			// A .NET extension method `static M(this T self, …)` exposed as a Kotlin extension `fun T.m()` on a @Clr
			// object: it's a STATIC call whose first argument is the extension receiver.
			val extRecv = extensionReceiver(call)
			if (isStatic && extRecv != null) {
				val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
				val allArgTypes = (listOf(str(netType(extRecv.type))) + regularArgs(call).map { str(netType(it.type)) }).joinToString(",")
				return """{"k":"clrStatic","type":${str(clrType)},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"args":[$allArgs]}"""
			}
			// A restored MEMBER extension function (`class C { fun T.f() }`): an INSTANCE method on the dispatch receiver
			// (C) whose first .NET param `__self` is the extension receiver -> dispatch on `recv`, prepend the receiver.
			if (!isStatic && extRecv != null && recv != null) {
				val allArgs = (listOf(expr(extRecv)) + regularArgs(call).map { expr(it) }).joinToString(",")
				val allArgTypes = (listOf(str(netType(extRecv.type))) + regularArgs(call).map { str(netType(it.type)) }).joinToString(",")
				return """{"k":"clrInstance","type":${str(memberType)},"method":${str(member)},"argTypes":[$allArgTypes],"ret":$ret,"recv":${expr(recv)},"args":[$allArgs]}"""
			}
			val (cArgs, cArgTypes) = clrCallArgs(call, callee)
			return if (isStatic)
				"""{"k":"clrStatic","type":${str(clrType)},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"args":[$cArgs]}"""
			else
				"""{"k":"clrInstance","type":${str(memberType)},"method":${str(member)},"argTypes":[$cArgTypes],"ret":$ret,"recv":${expr(recv!!)},"args":[$cArgs]}"""
		}

		// Companion-object member -> a static member of the enclosing class (precedes user-property field access).
		(callee.parent as? IrClass)?.takeIf { it.isCompanion }?.let { comp ->
			val enclosing = typeName(comp.parent as IrClass)
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) return if (callee === prop.setter)
				if (prop.backingField == null)
					"""{"k":"callStatic","owner":${str(enclosing)},"method":${str("set_" + prop.name.asString())},"args":[${regularArgs(call).joinToString(",") { expr(it) }}]}"""
				else
					"""{"k":"staticFieldSet","ownerType":${str(enclosing)},"name":${str(prop.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			else if (prop.backingField == null)
				"""{"k":"callStatic","owner":${str(enclosing)},"method":${str("get_" + prop.name.asString())},"args":[]${retHint(false, call.type)}}"""
			else """{"k":"staticField","ownerType":${str(enclosing)},"name":${str(prop.name.asString())}}"""
			return """{"k":"callStatic","owner":${str(enclosing)},"method":${str(name)}${overloadSigField(callee)},"args":[${filledArgs(call).joinToString(",")}]}"""
		}

			// An INJECTED top-level EXTENSION property (`val T.p` from a DotKt assembly) -> its get_/set_<name>(__self)
			// statics on the file class, with the extension receiver passed as `__self`. (body==null = injected stub.)
			(callee.correspondingPropertySymbol?.owner)?.let { p ->
				if (declaringClass == null) kotc.ClrTopLevelRegistry.lookupProp(p.fqNameWhenAvailable?.asString())?.let { fileClass ->
					val recv = extensionReceiver(call)
					if (callee === p.setter) {
						val args = listOfNotNull(recv) + regularArgs(call)
						return """{"k":"clrStatic","type":${str(fileClass)},"method":${str("set_" + p.name.asString())},"argTypes":[${args.joinToString(",") { str(netType(it.type)) }}],"ret":"System.Void","args":[${args.joinToString(",") { expr(it) }}]}"""
					}
					return """{"k":"clrStatic","type":${str(fileClass)},"method":${str("get_" + p.name.asString())},"argTypes":[${recv?.let { str(netType(it.type)) } ?: ""}],"ret":${str(netType(callee.returnType))},"args":[${recv?.let { expr(it) } ?: ""}]}"""
				}
			}

		// Top-level property (parent is the file/package, not a class) -> a static field of ITS DEFINING file's
		// class. Use the property's own file, NOT the file currently being emitted — else a cross-file reference
		// looks for `<ReferencingFile>Kt.prop` and fails (`field XKt.prop not found`; feedback item 11).
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			if (declaringClass == null) {
				val owner = fileClassOf(p)
				if (p.backingField == null) {
					val ext = extensionReceiver(call)
					return if (callee === p.setter) {
						val args = listOfNotNull(ext) + regularArgs(call)
						"""{"k":"callStatic","owner":${str(owner)},"method":${str("set_" + p.name.asString())},"args":[${args.joinToString(",") { expr(it) }}]}"""
					} else {
						"""{"k":"callStatic","owner":${str(owner)},"method":${str("get_" + p.name.asString())},"args":[${ext?.let { expr(it) } ?: ""}]${retHint(false, call.type)}}"""
					}
				}
				return if (callee === p.setter)
					"""{"k":"staticFieldSet","ownerType":${str(owner)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"staticField","ownerType":${str(owner)},"name":${str(p.name.asString())}}"""
			}
		}

		// `s.length` on a String -> System.String.Length (CLR property).
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			if (p.name.asString() == "length" && dispatchReceiver(call)?.type?.classFqName?.asString() == "kotlin.String")
				return """{"k":"clrPropGet","type":"System.String","name":"Length","retType":"System.Int32","static":false,"recv":${expr(dispatchReceiver(call)!!)}}"""
		}
		// Pair/Triple `.first`/`.second`/`.third` -> stdlib class fields.
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			val pfq = (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			if (pfq == "kotlin.Pair" || pfq == "kotlin.Triple") {
				val field = p.name.asString().takeIf { it in setOf("first", "second", "third") }
				if (field != null) dispatchReceiver(call)?.let { r ->
					return """{"k":"field","ownerType":${str(birType(r.type).removePrefix("@"))},"recv":${expr(r)},"name":${str(field)}}"""
				}
			}
			// `IndexedValue.index`/`.value` -> stdlib class fields.
			if (pfq == "kotlin.collections.IndexedValue") {
				val field = p.name.asString().takeIf { it in setOf("index", "value") }
				if (field != null) dispatchReceiver(call)?.let { r ->
					return """{"k":"field","ownerType":${str(birType(r.type).removePrefix("@"))},"recv":${expr(r)},"name":${str(field)}}"""
				}
			}
		}

		// Property get/set on a user class -> field access.
		val property = callee.correspondingPropertySymbol?.owner
		// kotlin.Result.isSuccess/isFailure getters (stdlib bodies absent, so they reach the generic property path) ->
		// the shared DotKt.Result<T> struct properties (T4 / docs §13n).
		if (property != null && declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.Result") {
			(dispatchReceiver(call) ?: extensionReceiver(call))?.let { r ->
				val pn = when (property.name.asString()) { "isSuccess" -> "IsSuccess"; "isFailure" -> "IsFailure"; else -> null }
				if (pn != null) return """{"k":"clrPropGet","type":${str(birType(r.type))},"name":${str(pn)},"retType":"bool","static":false,"recv":${expr(r)}}"""
			}
		}
		// `.size` -> CIL array length (arrays) or `Enumerable.Count` (collections).
		if (property?.name?.asString() == "size") dispatchReceiver(call)?.let { r ->
			if (isArrayType(r.type)) return """{"k":"arrayLen","array":${expr(r)}}"""
			// `Color.entries.size`: entries -> a Color[] (enumValues), so .size is the array length.
			if (r.type.classFqName?.asString() == "kotlin.enums.EnumEntries") return """{"k":"arrayLen","array":${expr(r)}}"""
			// kotlin.* collection/map `.size` is NOT intercepted: it's a real `size` property — fall through to the
			// ordinary property read so it emits as a kotlin.* `get_size` call.
		}
		// `kProperty.name` -> the compiler-generated KProperty.get_name().
		if (property?.name?.asString() == "name" &&
			declaringClass?.fqNameWhenAvailable?.asString()?.startsWith("kotlin.reflect.KProperty") == true) {
			needsKProperty = true
			val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
			return """{"k":"callInstance","ownerType":"<>dotkt_KProperty","virtual":true,"recv":$recv,"method":"get_name","args":[]}"""
		}
		// Delegated property access. `by lazy`: `obj.x` -> `obj.x$delegate.Value` (System.Lazy<T>.Value),
		// dropping thisRef/KProperty. Custom (duck-typed) delegate: route to its getValue/setValue, passing
		// thisRef and a materialized `KProperty` (compiler-generated). Stdlib-interface delegates -> deferred.
		if (property != null && property.isDelegated && declaringClass != null) {
			val bf = property.backingField
			val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
			val delegate = bf?.let { """{"k":"field","ownerType":${str(typeName(declaringClass))},"recv":$recv,"name":${str(it.name.asString())}}""" }
			if (callee === property.getter && bf?.type?.classFqName?.asString() == "kotlin.Lazy") {
				val elem = birType(callee.returnType)
				return """{"k":"clrPropGet","type":${str("clrg:System.Lazy[$elem]")},"name":"Value","retType":${str(elem)},"static":false,"recv":$delegate}"""
			}
			// `val x by map` is NOT intercepted: FIR routes it through the stdlib `Map.getValue`/`setValue` operator —
			// fall through to the getValue/setValue delegate routing so it emits as real kotlin.* calls.
			// Route getValue/setValue to the delegate object. The dispatch type is either the concrete user
			// delegate class (duck-typed or implementing Read(Write)Property) or — when the field is typed as
			// the Read(Write)Property interface (e.g. Delegates.observable) — the synthetic interface.
			val delegateClass = bf?.type?.classifierOrNull?.owner as? IrClass
			val isUserDelegate = delegateClass != null && clrName(delegateClass) == null &&
				delegateClass.fqNameWhenAvailable?.asString()?.startsWith("kotlin") != true
			val ownerName = when {
				isUserDelegate -> typeName(delegateClass!!)
				bf != null -> propIface(bf.type)   // ReadWriteProperty/ReadOnlyProperty-typed field
				else -> null
			}
			if (delegate != null && ownerName != null) {
				needsKProperty = true
				val owner = str(ownerName)
				val kprop = """{"k":"new","type":"<>dotkt_KPropertyImpl","args":[{"k":"const","type":"string","value":${str(property.name.asString())}}]}"""
				// callvirt: getValue/setValue is virtual (interface impl) or final (duck-typed) — callvirt fits both.
				return if (callee === property.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"setValue","args":[$recv,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"getValue","args":[$recv,$kprop]}"""
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
			if (hasCustomAccessor(property) || ifaceAcc != null || property.backingField == null) {
				val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
				// A MEMBER extension property (`class C { val T.p get() }`): dispatch on the enclosing C, but its `get_p`/
				// `set_p` method takes the extension receiver as a leading `__self` arg -> prepend it.
				val pExt = extensionReceiver(call)?.let { expr(it) }
				return if (callee === property.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(ifaceAcc ?: "set_" + property.name.asString())},"args":[${listOfNotNull(pExt, expr(regularArgs(call).first())).joinToString(",")}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":$virtual,"recv":$recv,"method":${str(ifaceAcc ?: "get_" + property.name.asString())},"args":[${pExt ?: ""}]${retHint('[' in ownerStr, call.type)}}"""
			}
			return if (callee === property.setter)
				"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			// `lateinit var` read -> throw if still uninitialized (the field is null) — proper lateinit semantics.
			else if (property.isLateinit)
				"""{"k":"lateinitGet","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}}"""
			else """{"k":"field","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())}${retHint('[' in ownerStr, call.type)}}"""
		}

		// Kotlin Any-methods on a builtin receiver -> System.Object virtuals (used by data-class hashCode/equals).
		if (isBuiltin && dispatchReceiver(call) != null) when (name) {
			"hashCode" -> return """{"k":"objMethod","method":"GetHashCode","recv":${expr(dispatchReceiver(call)!!)}}"""
			"toString" -> if (regularArgs(call).isEmpty()) return """{"k":"objMethod","method":"ToString","recv":${expr(dispatchReceiver(call)!!)}}"""
			"equals" -> return """{"k":"objMethod","method":"Equals","recv":${expr(dispatchReceiver(call)!!)},"arg":${expr(regularArgs(call).first())}}"""
		}
		// `n.toString(radix)` (Int/Long, a kotlin.text extension) -> System.Convert.ToString(value, base 2/8/10/16).
		if (name == "toString" && regularArgs(call).size == 1) {
			val recv = extensionReceiver(call) ?: dispatchReceiver(call)
			val rfq = recv?.type?.classFqName?.asString()
			if (recv != null && (rfq == "kotlin.Int" || rfq == "kotlin.Long")) {
				val vt = if (rfq == "kotlin.Long") "System.Int64" else "System.Int32"
				return """{"k":"clrStatic","type":"System.Convert","method":"ToString","argTypes":["$vt","System.Int32"],"ret":"System.String","args":[${expr(recv)},${expr(regularArgs(call).first())}]}"""
			}
		}

		if (isBuiltin) {
			val operands = call.arguments.filterNotNull()
			// `String + x` is concatenation, not numeric add.
			if (name == "plus" && declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.String" && operands.size == 2)
				return """{"k":"concat","parts":[${expr(operands[0])},${expr(operands[1])}]}"""
			// `==` (EQEQ): structural — `ceq` for primitives, null-safe `Object.Equals` for String/reference types.
			// `===` (EQEQEQ): always identity (`ceq`).
			if (name == "EQEQ" && operands.size == 2) {
				return if (isPrimitiveEqType(operands[0].type) && isPrimitiveEqType(operands[1].type))
					"""{"k":"bin","op":"==","l":${expr(operands[0])},"r":${expr(operands[1])}}"""
				else """{"k":"objEq","l":${expr(operands[0])},"r":${expr(operands[1])}}"""
			}
			if (name == "EQEQEQ" && operands.size == 2)
				return """{"k":"bin","op":"==","l":${expr(operands[0])},"r":${expr(operands[1])}}"""
			BINARY[name]?.let { op -> if (operands.size == 2) {
				// A boxed (Any) operand via an un-narrowed smart-cast (`x is Int && x > 10`) against a primitive:
				// cast it to the other operand's type so the numeric/compare op sees the right value, not the box.
				fun operand(o: IrExpression, other: IrExpression): String {
					val ot = birType(o.type); val tt = birType(other.type)
					return if (ot == "object" && tt != "object") """{"k":"cast","type":${str(tt)},"e":${expr(o)}}""" else expr(o)
				}
				return """{"k":"bin","op":${str(op)},"l":${operand(operands[0], operands[1])},"r":${operand(operands[1], operands[0])}}"""
			} }
			UNARY[name]?.let { if (operands.size == 1) return """{"k":"un","op":${str(it)},"e":${expr(operands[0])}}""" }
			// `i.inc()`/`i.dec()` (the `i++`/`i--` desugaring) -> `(i + 1)`/`(i - 1)`.
			if (name == "inc" && operands.size == 1) return """{"k":"bin","op":"+","l":${expr(operands[0])},"r":{"k":"const","type":"int","value":1}}"""
			if (name == "dec" && operands.size == 1) return """{"k":"bin","op":"-","l":${expr(operands[0])},"r":{"k":"const","type":"int","value":1}}"""
			// Numeric conversion `x.toLong()`/`x.toInt()`/… (numeric receiver) -> a CIL conv.
			NUMBER_CONV[name]?.let { to ->
				val recv = dispatchReceiver(call)
				if (recv != null && recv.type.classFqName?.asString() in NUMERIC_FQ)
					return """{"k":"conv","to":${str(to)},"e":${expr(recv)}}"""
			}
			val fq = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
			if (fq == "kotlin.io" && (name == "println" || name == "print")) {
				val m = if (name == "println") "WriteLine" else "Write"
				return """{"k":"console","method":${str(m)},"args":[${operands.joinToString(",") { expr(it) }}]}"""
			}
			// `readLine()` -> Console.ReadLine() (returns String?; null at EOF, like Kotlin).
			if (fq == "kotlin.io" && name == "readLine")
				return """{"k":"clrStatic","type":"System.Console","method":"ReadLine","argTypes":[],"ret":"System.String","args":[]}"""
			// Regex: `"p".toRegex()` -> new Regex("p"); `r.containsMatchIn(s)` -> r.IsMatch(s); `r.replace(s,rep)` -> r.Replace(s,rep).
			val RX = "System.Text.RegularExpressions.Regex"
			if (name == "toRegex") extensionReceiver(call)?.let { p ->
				return """{"k":"clrNew","type":${str(RX)},"argTypes":["System.String"],"args":[${expr(p)}]}"""
			}
			if ((name == "containsMatchIn" || name == "replace" || name == "matches" || name == "find") &&
				dispatchReceiver(call)?.type?.classFqName?.asString() == "kotlin.text.Regex") {
				val r = dispatchReceiver(call)!!; val a = regularArgs(call)
				return when (name) {
					"containsMatchIn" -> """{"k":"clrInstance","type":${str(RX)},"method":"IsMatch","argTypes":["System.String"],"ret":"System.Boolean","recv":${expr(r)},"args":[${expr(a[0])}]}"""
					"replace" -> """{"k":"clrInstance","type":${str(RX)},"method":"Replace","argTypes":["System.String","System.String"],"ret":"System.String","recv":${expr(r)},"args":[${expr(a[0])},${expr(a[1])}]}"""
					// matches = FULL match, find = first MatchResult-or-null -> the DotKt.Text.Regexes shims.
					"matches" -> """{"k":"clrStatic","type":"DotKt.Text.Regexes","method":"Matches","argTypes":[${str(RX)},"System.String"],"ret":"System.Boolean","args":[${expr(r)},${expr(a[0])}]}"""
					else -> """{"k":"clrStatic","type":"DotKt.Text.Regexes","method":"Find","argTypes":[${str(RX)},"System.String"],"ret":"clr:System.Text.RegularExpressions.Match","args":[${expr(r)},${expr(a[0])}]}"""
				}
			}
			// `"%d %s".format(a, b)` (printf) -> System.String.Format(translated, object[]{a,b}). Only a LITERAL
			// `.format` binds STRAIGHT to System.String.Format with .NET composite format strings — the user writes
			// `"{0:F2}".format(x)`, not Java's `"%.2f".format(x)`. DotKt does not reproduce java.util.Formatter (a
			// JVM-ism — Kotlin/Native and Kotlin/JS don't have String.format either); see [[discard-jvm-isms]]. Both
			// forms route here: instance `fmt.format(args)` (the receiver IS the format) and companion
			// `String.format(fmt, args)` (the format is the first arg; the receiver is String.Companion).
			if (name == "format") {
				val extRecv = extensionReceiver(call)
				val instanceForm = extRecv?.type?.classFqName?.asString() == "kotlin.String"
				val fmtExpr = if (instanceForm) extRecv else regularArgs(call).getOrNull(0)
				if (fmtExpr?.type?.classFqName?.asString() == "kotlin.String") {
					val fmtArgs = if (instanceForm) regularArgs(call) else regularArgs(call).drop(1)
					val elems = (fmtArgs.getOrNull(0) as? IrVararg)?.elements?.filterIsInstance<IrExpression>() ?: fmtArgs
					val arr = """{"k":"newArray","elem":"object","elems":[${elems.joinToString(",") { expr(it) }}]}"""
					return """{"k":"clrStatic","type":"System.String","method":"Format","argTypes":["System.String","array:object"],"ret":"System.String","args":[${expr(fmtExpr)},$arr]}"""
				}
			}
			// Exhaustive-when synthetic else / uninitialized property -> throw (the branch is unreachable).
			if (name == "noWhenBranchMatchedException" || name == "throwUninitializedPropertyAccessException")
				return throwExpr(newExc("System.InvalidOperationException", str(name)))
			// Precondition / error helpers (top-level kotlin.* functions).
			if (calleeFq == "kotlin.TODO") return throwExpr(newExc("System.NotImplementedException", null))
			if (calleeFq == "kotlin.error")
				return throwExpr("""{"k":"clrNew","type":"System.InvalidOperationException","argTypes":["System.String"],"args":[${regularArgs(call).firstOrNull()?.let { expr(it) } ?: """{"k":"const","type":"string","value":"error"}"""}]}""")
			if (calleeFq == "kotlin.require")
				return """{"k":"cond","cond":${expr(regularArgs(call).first())},"then":{"k":"const","type":"void","value":null},"else":${throwExpr(newExc("System.ArgumentException", "\"Failed requirement\""))}}"""
			if (calleeFq == "kotlin.check")
				return """{"k":"cond","cond":${expr(regularArgs(call).first())},"then":{"k":"const","type":"void","value":null},"else":${throwExpr(newExc("System.InvalidOperationException", "\"Check failed\""))}}"""
			if (name == "ieee754equals" && regularArgs(call).size == 2) {
				val a = regularArgs(call)
				return """{"k":"bin","op":"==","l":${expr(a[0])},"r":${expr(a[1])}}"""
			}
			// requireNotNull(x)/checkNotNull(x) -> evaluate once; throw if null, else the (non-null) value.
			if (calleeFq == "kotlin.requireNotNull" || calleeFq == "kotlin.checkNotNull") {
				val arg = regularArgs(call).first()
				val nv = "__rn${scopeCounter++}"
				val excType = if (calleeFq == "kotlin.requireNotNull") "System.ArgumentNullException" else "System.NullReferenceException"
				val velem = nullableElem(arg.type)
				val nvLoc = """{"k":"local","name":${str(nv)}}"""
				return if (velem != null) {
					// value-nullable T?: HasValue ? Value : throw.
					"""{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${str("nullable:$velem")},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"nullableHasValue","elem":${str(velem)},"e":$nvLoc},"then":{"k":"nullableValue","elem":${str(velem)},"e":$nvLoc},"else":${throwExpr(newExc(excType, "\"Required value was null\""))}}}"""
				} else {
					"""{"k":"valueBlock","stmts":[{"k":"var","name":${str(nv)},"type":${str(birType(arg.type))},"init":${expr(arg)}}],"result":{"k":"cond","cond":{"k":"un","op":"!","e":{"k":"objEq","l":$nvLoc,"r":{"k":"const","type":"void","value":null}}},"then":$nvLoc,"else":${throwExpr(newExc(excType, "\"Required value was null\""))}}}"""
				}
			}
			// coerceAtMost/atLeast/In -> System.Math.Min/Max/Clamp (receiver is the first arg).
			if (calleeFq == "kotlin.ranges.coerceAtMost" || calleeFq == "kotlin.ranges.coerceAtLeast" || calleeFq == "kotlin.ranges.coerceIn") {
				if (calleeFq == "kotlin.ranges.coerceIn" && regularArgs(call).size == 1) {
					val recv = extensionReceiver(call)!!
					val range = regularArgs(call).first()
					val rfq = range.type.classFqName?.asString()
					if (rfq != null && rfq.endsWith("Range")) {
						val tmp = "__rng${scopeCounter++}"
						val rangeType = birType(range.type)
						val owner = rangeType.removePrefix("@").substringBefore("[")
						val loc = """{"k":"local","name":${str(tmp)}}"""
						val first = """{"k":"field","ownerType":${str(owner)},"recv":$loc,"name":"first"}"""
						val last = """{"k":"field","ownerType":${str(owner)},"recv":$loc,"name":"last"}"""
						return """{"k":"valueBlock","stmts":[{"k":"var","name":${str(tmp)},"type":${str(rangeType)},"init":${expr(range)}}],"result":{"k":"clrStatic","type":"System.Math","method":"Clamp","argTypes":[${str(netType(recv.type))},${str(netType(recv.type))},${str(netType(recv.type))}],"ret":${str(netType(callee.returnType))},"args":[${expr(recv)},$first,$last]}}"""
					}
				}
				val m = when (calleeFq) { "kotlin.ranges.coerceAtMost" -> "Min"; "kotlin.ranges.coerceAtLeast" -> "Max"; else -> "Clamp" }
				val all = listOf(extensionReceiver(call)!!) + regularArgs(call)
				return """{"k":"clrStatic","type":"System.Math","method":${str(m)},"argTypes":[${all.joinToString(",") { str(netType(it.type)) }}],"ret":${str(netType(callee.returnType))},"args":[${all.joinToString(",") { expr(it) }}]}"""
			}
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
			// `kotlin.math.*` -> `System.Math.*` lowered to a `clrStatic` (ilemit resolves the overload by argTypes).
			if (fq == "kotlin.math") MATH_FUNCS[name]?.let { m ->
				val args = regularArgs(call)
				return """{"k":"clrStatic","type":"System.Math","method":${str(m)},"argTypes":[${args.joinToString(",") { str(netType(it.type)) }}],"ret":${str(netType(callee.returnType))},"args":[${args.joinToString(",") { expr(it) }}]}"""
			}
			if (fq == "kotlin.text") {
				// `s.repeat(n)` -> Concat(Repeat(s,n)); `s.reversed()` -> new string(Reverse(s).ToArray()).
				if (name == "repeat") (extensionReceiver(call) ?: dispatchReceiver(call))?.takeIf { it.type.classFqName?.asString() == "kotlin.String" }?.let { recv ->
					return """{"k":"strRepeat","s":${expr(recv)},"n":${expr(regularArgs(call).first())}}"""
				}
				if (name == "reversed") (extensionReceiver(call) ?: dispatchReceiver(call))?.takeIf { it.type.classFqName?.asString() == "kotlin.String" }?.let { recv ->
					return """{"k":"strReversed","s":${expr(recv)}}"""
				}
				// `s.split(",")` -> ToList(s.Split(string[] delimiters, StringSplitOptions.None)).
				if (name == "split") extensionReceiver(call)?.let { recv ->
					val seps = (regularArgs(call).firstOrNull() as? IrVararg)?.elements?.filterIsInstance<IrExpression>().orEmpty()
					return """{"k":"split","recv":${expr(recv)},"seps":[${seps.joinToString(",") { expr(it) }}]}"""
				}
				// Kotlin `substring(start, end)` takes an END index (exclusive); .NET `Substring(start, LENGTH)`.
				// Convert end -> (end - start). (1-arg `substring(start)` matches .NET Substring(start) as-is.)
				if (name == "substring" && regularArgs(call).size == 2) {
					val recv = extensionReceiver(call) ?: dispatchReceiver(call)
					if (recv != null) {
						val a = regularArgs(call)
						val len = """{"k":"bin","op":"-","l":${expr(a[1])},"r":${expr(a[0])}}"""
						return """{"k":"clrInstance","type":"System.String","method":"Substring","argTypes":["System.Int32","System.Int32"],"ret":"System.String","recv":${expr(recv)},"args":[${expr(a[0])},$len]}"""
					}
				}
				// String ops -> `System.String` instance methods (clrInstance; ilemit resolves overload).
				STRING_OPS[name]?.let { m ->
					val recv = extensionReceiver(call) ?: dispatchReceiver(call)
					if (recv != null) {
						val args = regularArgs(call)
						return """{"k":"clrInstance","type":"System.String","method":${str(m)},"argTypes":[${args.joinToString(",") { str(netType(it.type)) }}],"ret":${str(netType(callee.returnType))},"recv":${expr(recv)},"args":[${args.joinToString(",") { expr(it) }}]}"""
					}
				}
				// `"42".toInt()` -> `System.Int32.Parse(string)` (static, receiver passed as the arg).
				NUMBER_PARSE[name]?.let { t ->
					extensionReceiver(call)?.let { recv ->
						return """{"k":"clrStatic","type":${str(t)},"method":"Parse","argTypes":["System.String"],"ret":${str(netType(callee.returnType))},"args":[${expr(recv)}]}"""
					}
				}
				// `c.isDigit()`/`c.uppercaseChar()` -> `System.Char.X(char)` (static, receiver as the arg).
				CHAR_OPS[name]?.let { m ->
					extensionReceiver(call)?.let { recv ->
						return """{"k":"clrStatic","type":"System.Char","method":${str(m)},"argTypes":["System.Char"],"ret":${str(netType(callee.returnType))},"args":[${expr(recv)}]}"""
					}
				}
				// String predicates: isEmpty/isNotEmpty -> Length==0/!=0, isBlank/isNotBlank -> IsNullOrWhiteSpace.
				if (name == "isEmpty" || name == "isNotEmpty" || name == "isBlank" || name == "isNotBlank") {
					(extensionReceiver(call) ?: dispatchReceiver(call))?.takeIf { it.type.classFqName?.asString() == "kotlin.String" }?.let { recv ->
						val r = expr(recv)
						val len = """{"k":"clrPropGet","type":"System.String","name":"Length","retType":"System.Int32","static":false,"recv":$r}"""
						val blank = """{"k":"clrStatic","type":"System.String","method":"IsNullOrWhiteSpace","argTypes":["System.String"],"ret":"System.Boolean","args":[$r]}"""
						return when (name) {
							"isEmpty" -> """{"k":"bin","op":"==","l":$len,"r":{"k":"const","type":"int","value":0}}"""
							"isNotEmpty" -> """{"k":"bin","op":"!=","l":$len,"r":{"k":"const","type":"int","value":0}}"""
							"isBlank" -> blank
							else -> """{"k":"un","op":"!","e":$blank}"""
						}
					}
				}
			}
		}

		// (The old "unsupported external stdlib fn" guard — a COLLECTION_OPS-era seam that errored unless the receiver
		// was a List-backed collection — is gone: with pure kotlin.* the stdlib provides every fn, so calls route via
		// the round-trip path below or emit as ordinary kotlin.* calls.)
		// DotKt round-trip: a call to a top-level function restored from a [KotlinFile] facade in a referenced
		// assembly -> a .NET static call on that file-facade class. `body == null` distinguishes the injected symbol
		// from a same-named local top-level fun. (A suspend top-level fun awaits via the coroutine path, not here.)
		if (callee.body == null && dispatchReceiver(call) == null) {
			val extRecv = extensionReceiver(call)
			kotc.ClrTopLevelRegistry.lookup(callee.fqNameWhenAvailable?.asString())?.let { (fileClass, _) ->
				// A cross-module `inline fun` taking a lambda (body==null here = injected stub) -> splice its carried
				// [KotlinInline] body at this call site (the only way a non-local `return` through the lambda works).
				if (callee.isInline && hasLambdaArg(call) && extRecv == null) return inlineSpliceCall(call, fileClass)
				// An extension fun: its receiver is the .NET method's first param (`__self`), so prepend it to the args.
				val a = listOfNotNull(extRecv) + filledArgExprs(call)   // fill omitted default args (trailing/named-middle/reordered)
				// A GENERIC top-level fun (e.g. a `reified` inline restored as a generic method) -> a generic static
				// call carrying the type args, so ilemit MakeGenericMethods it (the reified `typeof(T)`/`is T` body
				// then sees the concrete type). CLR generics are reified, so no inlining is needed across assemblies.
				if (callee.typeParameters.isNotEmpty()) {
					val targs = callee.typeParameters.indices.map { call.typeArguments.getOrNull(it) }
					if (targs.all { it != null }) {
						val taJson = targs.joinToString(",") { str(birType(it!!)) }
						// `shapes` must line up with `a` (= extension receiver, then regular args), so a GENERIC extension
						// fun's `__self` receiver shape is included — else ilemit's by-shape overload pick finds 0 params.
						val shapeParams = (if (extRecv != null) listOf(callee.parameters.first { it.kind == IrParameterKind.ExtensionReceiver }) else emptyList()) + regularParams(callee)
						val shapes = shapeParams.joinToString(",") { str(clrMethodShape(it.type)) }
						return """{"k":"clrGenericStatic","type":${str(fileClass)},"method":${str(name)},"typeArgs":[$taJson],"shapes":[$shapes],"args":[${a.joinToString(",") { expr(it) }}]}"""
					}
				}
				// A suspend top-level fun's .NET method returns Task<T> (awaited by the coroutine machinery via expr(call)).
				val ret = if (callee.isSuspend) coTaskType(call.type) else netType(callee.returnType)
				return """{"k":"clrStatic","type":${str(fileClass)},"method":${str(name)},"argTypes":[${a.joinToString(",") { str(netType(it.type)) }}],"ret":${str(ret)},"args":[${a.joinToString(",") { expr(it) }}]}"""
			}
		}
		// Fill omitted constant default arguments at the call site (IL methods have no default mechanism).
		val args = filledArgs(call).joinToString(",")
		// A generic method `fun <T> id(...)` -> carry the resolved type args so ilemit can MakeGenericMethod.
		val ta = typeArgsJson(call)
		// A call to a `suspend fun` resolves to its kickoff, which returns `Task<T>` (not the result T). The retType
		// hint (used by ilemit when typeArgs are present) must reflect that, else an awaited generic suspend call
		// is typed as the result T and `GetAwaiter` can't be found. See docs §13k.
		val effRet = if (callee.isSuspend) coTaskType(call.type) else birType(call.type)
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
				return """{"k":"callInstance","ownerType":${str(ownerStr)},"virtual":$virtual,"recv":${expr(recv)},"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty() || '[' in ownerStr, effRet)},"args":[$all]}"""
			}
			return """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$all]}"""
		}
		// Instance method on a user class, or a sibling top-level call.
		return if (recv != null) {
			// `it.hasNext()`/`it.next()` on a Kotlin iterator, `xs.iterator()` on a Kotlin iterable -> dispatch on the
			// monomorphized synthetic interface (KIterator_<elem> / KIterable_<elem>).
			(iteratorElemIface(recv.type) ?: iterableElemIface(recv.type))?.let { ifaceName ->
				return """{"k":"callInstance","ownerType":${str(ifaceName)},"virtual":true,"recv":${expr(recv)},"method":${str(name)},"args":[$args]}"""
			}
			val ownerStr = ownerSpec(declaringClass, recv.type)
			val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
			// A call to an override of a .NET-mapped interface member (e.g. a user Continuation's resumeWith) uses
			// the .NET member name (ResumeWith), matching what the class emitted.
			val mname = clrIfaceMemberName(callee) ?: objectMethodName(callee) ?: name
			"""{"k":"callInstance","ownerType":${str(ownerStr)},"virtual":$virtual,"recv":${expr(recv)},"method":${str(mname)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty() || '[' in ownerStr, effRet)},"args":[$args]}"""
		} else """{"k":"callStatic","owner":null,"method":${str(name)}${overloadSigField(callee)}$ta${retHintStr(ta.isNotEmpty(), effRet)},"args":[$args]}"""
	}

	/**
	 * `,"retType":"int"` for a generic call/member access: the concrete result type is known here (FIR-resolved
	 * `call.type`), so ilemit need not reflect the un-baked builder's return type (which stays `!0`/`!!0` and
	 * would mis-drive value-type boxing). Only emitted for the generic/constructed paths to stay non-invasive.
	 */
	internal fun retHint(generic: Boolean, t: IrType): String =
		if (generic) ""","retType":${str(birType(t))}""" else ""

	/** Like [retHint] but with a pre-computed return-type string (e.g. a suspend call's kickoff `Task<T>`). */
	internal fun retHintStr(generic: Boolean, retStr: String): String =
		if (generic) ""","retType":${str(retStr)}""" else ""

	/** `,"typeArgs":["int"]` when the callee is a generic method (its own type params resolved at this call). */
	internal fun typeArgsJson(call: IrCall): String {
		val tps = call.symbol.owner.typeParameters
		if (tps.isEmpty()) return ""
		val args = tps.indices.map { call.typeArguments.getOrNull(it) }
		if (args.any { it == null }) return ""
		return ""","typeArgs":[${args.joinToString(",") { str(birType(it!!)) }}]"""
	}

	/**
	 * The .NET name for a type/member: from a `@Clr("...")` annotation, or — for an S5 FIR-injected .NET type
	 * (synthesized into FIR without annotations) — from the [ClrTypeRegistry] the frontend populated. The IL
	 * backend, like the C# backend, must consult the registry so injected types resolve as real .NET types
	 * (otherwise they leak in as user classes and their members mis-route as fields). See [[s5-fir-injection-seam]].
	 */
	internal fun clrName(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): String? {
		for (a in decl.annotations) {
			if ((a as? IrConstructorCall)?.type?.classFqName?.asString() == "clr.Clr")
				return (a.arguments.firstOrNull() as? IrConst)?.value as? String
		}
		return (decl as? IrClass)?.fqNameWhenAvailable?.asString()?.let { kotc.ClrTypeRegistry.dotNetName(it) }
	}

	/** A type's fully-qualified .NET name, for IL reflection-based member resolution. */
	internal fun netType(t: IrType): String = when (val fq = t.classFqName?.asString()) {
		// The intrinsic `ClrRef<T>` is a managed reference -> `byref:<T>` (selects the out/ref overload in ilemit).
		"ClrRef" -> "byref:" + ((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { netType(it) }.orEmpty()
		// The intrinsic `Span<T>` -> the real `System.Span<T>`.
		"Span" -> "clrg:System.Span[" + (((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { netType(it) } ?: "object") + "]"
		"kotlin.Int" -> "System.Int32"
		"kotlin.Long" -> "System.Int64"
		"kotlin.Short" -> "System.Int16"
		"kotlin.Byte" -> "System.SByte"
		"kotlin.Double" -> "System.Double"
		"kotlin.Float" -> "System.Single"
		"kotlin.Boolean" -> "System.Boolean"
		"kotlin.Char" -> "System.Char"
		"kotlin.String" -> "System.String"
		"kotlin.Unit" -> "System.Void"
		"kotlinx.coroutines.CancellableContinuation" -> "clrg:DotKtx.Coroutines.CancellableCont[${firstArgNet(t)}]"
		"kotlin.Result" -> "clrg:DotKt.Result[${firstArgNet(t)}]"
		"kotlin.sequences.Sequence" -> "clrg:System.Collections.Generic.IEnumerable[" + (((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { netType(it) } ?: "object") + "]"
		"kotlinx.atomicfu.AtomicInt" -> "DotKtx.Atomicfu.AtomicInt"
		"kotlinx.atomicfu.AtomicLong" -> "DotKtx.Atomicfu.AtomicLong"
		"kotlinx.atomicfu.AtomicBoolean" -> "DotKtx.Atomicfu.AtomicBoolean"
		"kotlinx.atomicfu.AtomicRef" -> "clrg:DotKtx.Atomicfu.AtomicRef[" + (((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { netType(it) } ?: "object") + "]"
		else -> NET_EXCEPTIONS[fq]
			?: (t.classifierOrNull?.owner as? IrClass)?.let { clrName(it) }
			?: "System.Object"
	}

	internal fun paramNetTypes(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
		callee.parameters.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { str(netType(it.type)) }

	/** The `byref(x)` marker intrinsic wrapping an arg -> the inner lvalue `x`; else null. */
	internal fun byrefMarker(a: IrExpression): IrExpression? =
		if (a is IrCall && a.symbol.owner.name.asString() == "byref") regularArgs(a).firstOrNull() else null

	/** (argsJson, argTypesJson) for an injected .NET call. A `ClrRef<T>` param already maps to `byref:T` via netType
	 *  (so the out/ref overload resolves + optional params still default-fill); a `byref(x)` arg unwraps to its lvalue
	 *  `x`, which ilemit passes by address (EmitArg routes an IsByRef param through EmitAddr). */
	internal fun clrCallArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression, callee: org.jetbrains.kotlin.ir.declarations.IrFunction): Pair<String, String> {
		val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
		val tj = params.map { str(netType(it.type)) }
		val aj = regularArgs(call).map { val inner = byrefMarker(it); if (inner != null) expr(inner) else expr(it) }
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

	/** Kotlin `Array<T>` / primitive arrays -> a BIR `array:<elem>` type (ilemit -> `T[]`). */
	internal fun isArrayType(t: IrType): Boolean {
		val fq = t.classFqName?.asString()
		return fq == "kotlin.Array" || fq in PRIMITIVE_ARRAY_ELEM
	}

	internal fun arrayElemType(t: IrType): String {
		val fq = t.classFqName?.asString()
		PRIMITIVE_ARRAY_ELEM[fq]?.let { return it }
		if (fq == "kotlin.Array")
			return (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: "object"
		return "object"
	}

	/** (keyType, valType) BIR types of a Map<K,V>. */
	internal fun mapKV(t: IrType): Pair<String, String> {
		val a = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
		return (a.getOrNull(0) ?: "object") to (a.getOrNull(1) ?: "object")
	}

	/** Kotlin nullable VALUE type (`Int?`/`Double?`…) -> the BIR element type (int/double…), else null. */
	internal fun nullableElem(t: IrType): String? =
		if (t.isMarkedNullable()) VALUE_PRIM_BIR[t.classFqName?.asString()] else null

	/** Kotlin visibility -> BIR access keyword (public/private/internal/protected). */
	internal fun visOf(d: IrDeclarationWithVisibility): String = when (d.visibility.delegate) {
		Visibilities.Private, Visibilities.PrivateToThis -> "private"
		Visibilities.Internal -> "internal"
		Visibilities.Protected -> "protected"
		else -> "public"
	}

	/** Non-nullable primitive whose `==` is CIL `ceq` (else `==` is structural `Object.Equals`). */
	internal fun isPrimitiveEqType(t: IrType): Boolean =
		!t.isMarkedNullable() && t.classFqName?.asString() in PRIMITIVE_EQ_FQ

	/** A Kotlin `Any`-override -> its System.Object method name (`toString`->`ToString`…), else null. */
	internal fun objectMethodName(fn: IrSimpleFunction): String? {
		val reg = fn.parameters.count { it.kind == IrParameterKind.Regular }
		return when (fn.name.asString()) {
			"toString" -> if (reg == 0) "ToString" else null
			"hashCode" -> if (reg == 0) "GetHashCode" else null
			"equals" -> if (reg == 1) "Equals" else null
			else -> null
		}
	}

	/** The first type argument of a constructed type, as a generic-arg-safe spec: a `Unit` argument erases to the
	 *  real `DotKt.Unit` (a CLR generic arg can't be `void`/`System.Void`); else birType/netType. T7. */
	internal fun firstArgBir(t: IrType): String = ((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type
		?.let { if (it.isUnit()) "clr:DotKt.Unit" else birType(it) } ?: "object"
	internal fun firstArgNet(t: IrType): String = ((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type
		?.let { if (it.isUnit()) "clr:DotKt.Unit" else netType(it) } ?: "object"

	internal fun birType(t: IrType): String {
		// A type parameter `T` is a real generic parameter -> `gp:<name>` (resolved in IL context). On the CLR,
		// generics are reified, so even `reified T` rides on this (no inlining) — see [[clr-not-jvm-discard-jvmisms]].
		(t.classifierOrNull as? org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol)?.let { tp ->
			// While splicing an `inline fun`'s body, its OWN type params are substituted with the call's type arguments
			// (the splice site has no such param in scope) — e.g. `all<T>` spliced into `containsAll` resolves `T` to the
			// inferred element type. A `*` star projection lands here as the param itself -> render `object` (Any?).
			val nm = tp.owner.name.asString()
			typeArgSubst[nm]?.let { return it }
			return "gp:" + nm
		}
		// The intrinsic `ClrRef<T>` -> `byref:T` (a managed reference; a ref-cell delegate local is a `ref T` local).
		if (t.classFqName?.asString() == "ClrRef")
			return "byref:" + ((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { birType(it) }.orEmpty()
		// The intrinsic `Span<T>` -> the real `System.Span<T>`.
		if (t.classFqName?.asString() == "Span")
			return "clrg:System.Span[" + (((t as? IrSimpleType)?.arguments?.firstOrNull() as? IrTypeProjection)?.type?.let { birType(it) } ?: "object") + "]"
		// Nullable value type `Int?` -> System.Nullable<int> (reference nullables stay as the ref type).
		nullableElem(t)?.let { return "nullable:$it" }
		if (isArrayType(t)) return "array:" + arrayElemType(t)
		val fqp = t.classFqName?.asString()
		// kotlin.text.Regex -> System.Text.RegularExpressions.Regex.
		if (fqp == "kotlin.text.Regex") return "clr:System.Text.RegularExpressions.Regex"
		if (fqp == "kotlin.text.MatchResult") return "clr:System.Text.RegularExpressions.Match"
		// Kotlin/Java throwables -> their .NET counterpart (the common base; `.message` -> .Message). Covers a custom
		// exception base (`class E : Exception(msg)`) as well as a `Throwable`-typed value.
		if (fqp != null) NET_EXCEPTIONS[fqp]?.let { return "clr:$it" }
		if (fqp == "kotlin.AutoCloseable" || fqp == "java.lang.AutoCloseable" || fqp == "java.io.Closeable" || fqp == "kotlin.io.Closeable")
			return "clr:System.IDisposable"
		// kotlin.CharSequence -> a synthetic interface (no faithful .NET equivalent). See charSeqIface.
		charSeqIface(t)?.let { return "@$it" }
		// A function type as a value (e.g. a `block: suspend (P)->R` parameter): `kotlin.FunctionN` -> Func/Action,
		// `kotlin.coroutines.SuspendFunctionN` -> Func<P..,Task<R>> (suspend lambdas are Func<..,Task<R>> in the ABI).
		if (fqp != null && (fqp.startsWith("kotlin.coroutines.SuspendFunction") || fqp.startsWith("kotlin.Function"))) {
			val suspend = fqp.startsWith("kotlin.coroutines.SuspendFunction")
			val args = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
			if (args.isNotEmpty()) {
				val ret = args.last(); val ps = args.dropLast(1)
				val retEnc = if (suspend) coTaskType(ret) else if (ret.isUnit()) "void" else birTypeDeleg(ret)
				return "func:$retEnc:${ps.joinToString(",") { birTypeDeleg(it) }}"
			}
		}
		if (fqp == "kotlinx.coroutines.CancellableContinuation") return "clrg:DotKtx.Coroutines.CancellableCont[${firstArgBir(t)}]"
		// kotlinx.atomicfu atomics -> the DotKt.Coroutines Interlocked/Volatile wrappers (Phase 3 / §13a res. 5).
		if (fqp == "kotlinx.atomicfu.AtomicInt") return "clr:DotKtx.Atomicfu.AtomicInt"
		if (fqp == "kotlinx.atomicfu.AtomicLong") return "clr:DotKtx.Atomicfu.AtomicLong"
		if (fqp == "kotlinx.atomicfu.AtomicBoolean") return "clr:DotKtx.Atomicfu.AtomicBoolean"
		if (fqp == "kotlinx.atomicfu.AtomicRef") {
			val arg = (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			return "clrg:DotKtx.Atomicfu.AtomicRef[$arg]"
		}
		// kotlin.Result<T> -> the shared DotKt.Result<T> struct (one type, cross-assembly identity, so it
		// serves both runCatching AND the Continuation.resumeWith parameter). See docs §13n.
		if (fqp == "kotlin.Result") return "clrg:DotKt.Result[${firstArgBir(t)}]"
		// `by lazy` delegate: kotlin.Lazy<T> -> System.Lazy<T>.
		if (fqp == "kotlin.Lazy") {
			val elem = (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			return "clrg:System.Lazy[$elem]"
		}
		// kotlin.reflect.KProperty* (delegated-property metadata) -> the synthetic compiler-generated `KProperty`.
		if (fqp != null && (fqp.startsWith("kotlin.reflect.KProperty") || fqp.startsWith("kotlin.reflect.KMutableProperty"))) {
			needsKProperty = true; return "@<>dotkt_KProperty"
		}
		// kotlin.properties.Read(Write)Property<T,V> -> the monomorphized synthetic interface.
		propIface(t)?.let { return "@$it" }
		// Kotlin function type `(A,B)->R` (kotlin.FunctionN<A,B,R>) and a callable-reference type `KFunctionN<…>`
		// (the inferred type of `obj::method`/`::foo`) -> a BIR `func:<ret>:<args>` (Func/Action delegate).
		val fqn = t.classFqName?.asString()
		if (fqn != null && (fqn.startsWith("kotlin.Function") || fqn.startsWith("kotlin.reflect.KFunction"))) {
			val tys = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type }
			if (tys.isNotEmpty()) {
				val retT = tys.last()
				val ret = if (retT.isUnit()) "void" else birType(retT)
				return "func:$ret:${tys.dropLast(1).joinToString(",") { birType(it) }}"
			}
		}
		when (t.classFqName?.asString()) {
			"kotlin.Unit", "kotlin.Nothing" -> return "void"
			"kotlin.Any" -> return "object"
			"kotlin.Int" -> return "int"
			"kotlin.Long" -> return "long"
			"kotlin.Short" -> return "short"
			"kotlin.Byte" -> return "byte"
			"kotlin.Double" -> return "double"
			"kotlin.Float" -> return "float"
			"kotlin.Boolean" -> return "bool"
			"kotlin.Char" -> return "char"
			"kotlin.String" -> return "string"
			// Unsigned types (Kotlin inline classes) -> the native CLR unsigned primitives. The frontend already
			// lowers unsigned arithmetic to plain ops and stores the bit-pattern in the const value.
			"kotlin.UInt" -> return "uint"
			"kotlin.ULong" -> return "ulong"
			"kotlin.UByte" -> return "ubyte"
			"kotlin.UShort" -> return "ushort"
		}
		// A (Mutable)Iterator<E>/Iterable<E> whose ELEMENT is a type parameter (gp:E) can't be a monomorphized synthetic:
		// the synthetic interface `<>dotkt_KIterator_gp_E` would bake `gp:E` into a NON-generic type, so `next(): gp:E` has
		// no `E` to resolve at emit (the keystone bug blocking a generic `class HashSet<E>`'s `iterator(): MutableIterator<E>`
		// and `AbstractCollection<E>`). Map straight to the CLR-native generic IEnumerator<E>/IEnumerable<E> instead — which
		// is the right target anyway (roadmap step 3, the IEnumerable<->Iterator read-as). Concrete elements keep the
		// monomorphized synthetic below (the IL-can't-define-a-generic-interface workaround for user iterator classes).
		val klass = t.classifierOrNull?.owner as? IrClass
		// A @Clr / FIR-injected .NET type ("clr:System.Text.StringBuilder"); a constructed generic .NET type
		// (`Collection<Int>`) carries its concrete args as `clrg:<openName>[int]`.
		klass?.let { clrName(it) }?.let { netName ->
			val args = (t as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			return if (args.isNullOrEmpty()) "clr:$netName" else "clrg:$netName[${args.joinToString(",")}]"
		}
		// Enums -> the real .NET enum type reference (package-qualified, like other user types).
		if (klass != null && klass.kind == ClassKind.ENUM_CLASS) return "@" + typeName(klass)
		// A user-declared class/interface becomes a reference to that BIR type ("@Name"); a constructed user
		// generic carries concrete args ("@Box[int]"). Anon objects resolve through `typeName`.
		if (klass != null && (klass.kind == ClassKind.CLASS || klass.kind == ClassKind.INTERFACE)) {
			if (klass.typeParameters.isNotEmpty()) {
				// Carry concrete args ("@Box[int]"); a type-parameter arg rides on its `gp:T` form (resolvable in the
				// enclosing generic context) rather than collapsing to the open type — so `State<T>` as a generic
				// factory's return type stays constructed and the emitted IL is verifiable (item 13).
				val args = (t as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }
				if (!args.isNullOrEmpty())
					return "@" + typeName(klass) + "[" + args.joinToString(",") { birType(it) } + "]"
			}
			return "@" + typeName(klass)
		}
		return "object"
	}

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
