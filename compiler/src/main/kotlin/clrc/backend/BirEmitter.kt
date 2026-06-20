package clrc.backend

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
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import java.io.File

/**
 * D1.1 — Backend IR (BIR) emitter.
 *
 * Serializes the M0 subset of a file to a compact JSON the future `ilemit` tool consumes to emit
 * CIL directly (no C# in between). The IR walk mirrors [CSharpCodegen]; only the rendering target
 * differs (a structured AST as JSON instead of C# text). Stack lowering is deferred to ilemit.
 *
 * Scope (M0): top-level functions; const/local/binop/unop/call/concat/ternary; var/set/return/
 * while/if. Classes & interop are later milestones (D1.4+).
 */
@OptIn(UnsafeDuringIrConstructionAPI::class)
class BirEmitter {

	// Inline substitutions for synthetic temporaries (e.g. a `when` subject) in expression position.
	private val valSubst = HashMap<String, String>()

	// Lambda lifting: non-capturing lambdas become named static methods appended to the file class;
	// capturing lambdas become synthesized closure classes appended to the file's types.
	private val liftedMethods = ArrayList<String>()
	private val liftedTypes = ArrayList<String>()
	private var lambdaCounter = 0
	private var closureCounter = 0
	// CFG block-IR (E-0.5): file-global unique label ids (never reset) so ids never collide across methods/lambdas.
	private var cfgLabelN = 0
	private fun cfgFresh(): Int = cfgLabelN++
	// Inlining ([[function-inlining-spike]]): lambda params currently being inlined -> the lambda passed for them.
	private val inlineLambdas = java.util.IdentityHashMap<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration, IrFunctionExpression>()
	private var inlCounter = 0
	private var scopeCounter = 0
	private var fileClass = ""   // current file's static class name (for top-level property access)
	// Local functions: lifted to file-class statics; captured vars become leading params (calls prepend them).
	private val localFns = HashMap<org.jetbrains.kotlin.ir.declarations.IrFunction, Pair<String, List<IrValueDeclaration>>>()

	// Anonymous objects (`object : I { }`) are lifted to synthetic top-level classes. Their IR name is
	// "<no name provided>" (not a valid IL identifier), so map the IrClass identity -> its assigned name;
	// every self-reference (ownerType / `@<no name>` type) is routed through `typeName`.
	private val anonNames = java.util.IdentityHashMap<IrClass, String>()
	// Captured outer values inside a capturing object literal -> `this.<field>`. Keyed by value-declaration
	// IDENTITY (not name): the anon's own `<this>` and a captured outer `<this>` share the name "<this>".
	private val captureSubst = java.util.IdentityHashMap<IrValueDeclaration, String>()
	// A local delegated property's getter/setter function -> the IrLocalDelegatedProperty, so call() rewrites a
	// `<get-x>`/`<set-x>` call to access on the delegate local (mirrors the member-property delegate path).
	private val localDelegates = java.util.IdentityHashMap<IrSimpleFunction, IrLocalDelegatedProperty>()
	// Synthetic monomorphized interfaces for the Kotlin iterator protocol. IL can't define a generic
	// interface yet, so per concrete element type we emit a non-generic `KIterator_<elem>` with
	// `hasNext():bool` / `next():<elem>` (Codex-advised monomorphization). elemBir -> interface name.
	private val iterIfaces = LinkedHashMap<String, String>()
	// A custom (non-lazy) delegated property passes a `KProperty<*>` to getValue/setValue. KProperty has no
	// BCL equivalent (pure binding), so — like Kotlin/JVM's PropertyReferenceImpl — we compiler-generate a
	// minimal `KProperty` interface (`name`) + `KPropertyImpl(name)` class into the user's assembly.
	private var needsKProperty = false

	/** A user/anon class's emitted name (anon "<no name provided>" -> its synthetic lifted name). */
	private fun typeName(k: IrClass): String = anonNames[k] ?: k.name.asString()

	// Synthesized stdlib delegate classes for Delegates.observable/vetoable/notNull (their stdlib bodies are
	// absent from our IR, so we compiler-generate equivalents, monomorphized by value type, each implementing
	// the synthetic RWProperty_<V>). Keyed "<kind>:<V>" -> class name; defs accumulated for emission.
	private val synthDelegates = LinkedHashMap<String, String>()
	private val synthDelegateDefs = ArrayList<String>()

	/** Register (once) a synthesized observable/vetoable/notNull delegate class for value type V; return its name. */
	private fun synthDelegate(kind: String, v: String): String = synthDelegates.getOrPut("$kind:$v") {
		needsKProperty = true
		val safe = v.replace(Regex("[^A-Za-z0-9]"), "_")
		val cname = "${kind}Delegate_$safe"
		val iface = propIface0("kotlin.properties.ReadWriteProperty", v)   // RWProperty_<V>; registers it
		val thisRef = """{"name":"thisRef","type":"object"}"""
		val kp = """{"name":"property","type":"@KProperty"}"""
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
	private fun kPropertyDefs(): List<String> {
		if (!needsKProperty) return emptyList()
		val ifaceName = """{"name":"get_name","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":"string","body":[]}"""
		val iface = """{"name":"KProperty","kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$ifaceName]}"""
		val getName = """{"name":"get_name","static":false,"override":false,"virtual":true,"objectOverride":false,"vis":"public","params":[],"ret":"string","body":[{"k":"return","value":{"k":"field","ownerType":"KPropertyImpl","recv":{"k":"this"},"name":"name"}}]}"""
		val ctorBody = """{"k":"setField","ownerType":"KPropertyImpl","recv":{"k":"this"},"name":"name","value":{"k":"local","name":"name"}}"""
		val impl = """{"name":"KPropertyImpl","kind":"class","vis":"public","base":null,"interfaces":["KProperty"],"fields":[{"name":"name","type":"string"}],"ctors":[{"params":[{"name":"name","type":"string"}],"baseArgs":null,"thisArgs":null,"vis":"public","body":[$ctorBody]}],"methods":[$getName]}"""
		return listOf(iface, impl)
	}

	private fun kIteratorName(elemBir: String): String =
		iterIfaces.getOrPut(elemBir) { "KIterator_" + elemBir.replace(Regex("[^A-Za-z0-9]"), "_") }

	/** `kotlin.collections.(Mutable)Iterator<E>` -> the monomorphized synthetic interface name, else null. */
	private fun iteratorElemIface(t: IrType): String? {
		val fq = t.classFqName?.asString() ?: return null
		if (fq != "kotlin.collections.Iterator" && fq != "kotlin.collections.MutableIterator") return null
		val elem = (t as? IrSimpleType)?.arguments?.firstOrNull()
			?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
		return kIteratorName(elem)
	}

	// kotlin.properties.Read(Write)Property<T,V> -> monomorphized-by-V synthetic interfaces (like the iterator
	// protocol). The user delegate class implements one of these; getValue/setValue take (thisRef, KProperty[, V]).
	private val roPropIfaces = LinkedHashMap<String, String>()   // V (birType) -> interface name
	private val rwPropIfaces = LinkedHashMap<String, String>()

	/** `kotlin.properties.Read(Write)Property<T,V>` -> the monomorphized synthetic interface name, else null. */
	private fun propIface(t: IrType): String? {
		val fq = t.classFqName?.asString() ?: return null
		if (fq != "kotlin.properties.ReadWriteProperty" && fq != "kotlin.properties.ReadOnlyProperty") return null
		val v = (t as? IrSimpleType)?.arguments?.getOrNull(1)?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
		return propIface0(fq, v)
	}

	/** Register (once) the synthetic Read(Write)Property interface for value type `v`; return its name. */
	private fun propIface0(fq: String, v: String): String {
		needsKProperty = true
		val safe = v.replace(Regex("[^A-Za-z0-9]"), "_")
		return if (fq == "kotlin.properties.ReadWriteProperty") rwPropIfaces.getOrPut(v) { "RWProperty_$safe" }
		else roPropIfaces.getOrPut(v) { "ROProperty_$safe" }
	}

	/** BIR defs for every synthesized Read(Write)Property interface (getValue/setValue over (thisRef, KProperty)). */
	private fun propIfaceDefs(): List<String> {
		fun m(name: String, params: String, ret: String) =
			"""{"name":${str(name)},"static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[$params],"ret":${str(ret)},"body":[]}"""
		val kp = """{"name":"property","type":"@KProperty"}"""
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
	private fun iteratorIfaceDefs(): List<String> = iterIfaces.entries.map { (elem, name) ->
		val hasNext = """{"name":"hasNext","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":"bool","body":[]}"""
		val next = """{"name":"next","static":false,"override":false,"virtual":false,"objectOverride":false,"vis":"public","params":[],"ret":${str(elem)},"body":[]}"""
		"""{"name":${str(name)},"kind":"interface","base":null,"fields":[],"ctors":[],"methods":[$hasNext,$next]}"""
	}

	private val SCOPE_FUNCTIONS = setOf("kotlin.let", "kotlin.run", "kotlin.with", "kotlin.apply", "kotlin.also")

	fun emitFile(file: IrFile): String {
		// The `@ClrAwait` await intrinsic (`fun <T> Task<T>.await(): T`) is never emitted as a real method —
		// its call sites are lowered to coroutine suspension points (see suspendMethod). Skip it.
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>().filter { !isAwaitIntrinsic(it) }
		val classes = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.CLASS && clrName(it) == null }
		val interfaces = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.INTERFACE && clrName(it) == null }
		val topProps = file.declarations.filterIsInstance<IrProperty>()
		if (functions.isEmpty() && classes.isEmpty() && interfaces.isEmpty() && topProps.isEmpty()) return ""
		val className = File(file.fileEntry.name).name.removeSuffix(".kt")
			.replaceFirstChar { it.uppercaseChar() } + "Kt"
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
		val enums = file.declarations.filterIsInstance<IrClass>().filter { it.kind == ClassKind.ENUM_CLASS }
		// Basic enums -> real CLR enums (int-backed, for .NET interop); rich enums -> plain singleton classes.
		val (richEnums, basicEnums) = enums.partition { isRichEnum(it) }
		// Nested (non-inner) classes -> flattened to top-level synthetic types (they keep their real name, so
		// `new Nested(...)` and field ownerTypes resolve). `inner` classes need outer-`this` capture (deferred).
		val nested = classes.flatMap { nestedClasses(it) }
		// `inner class`es flatten to top-level types that capture the enclosing instance (`__outer`).
		val inners = classes.flatMap { innerClasses(it) }
		val typeDefs = basicEnums.map { enumDef(it) } + interfaces.map { interfaceDef(it) } +
			classes.map { typeDef(it) } + nested.map { typeDef(it) } + inners.map { innerClassDef(it) } +
			richEnums.map { richEnumDef(it) }
		val methods = (fnMethods + liftedMethods).joinToString(",")
		// Synthetic types (iterator/Read(Write)Property interfaces, synthesized Delegates.* classes, KProperty)
		// are registered lazily while emitting bodies above -> append last (order matters: producers before
		// kPropertyDefs/propIfaceDefs, which read flags/maps the producers populate).
		val synthDelegateTypes = synthDelegateDefs.joinToString(",").let { if (it.isEmpty()) emptyList() else listOf(it) }
		val types = (typeDefs + liftedTypes + synthDelegateTypes + iteratorIfaceDefs() + propIfaceDefs() + kPropertyDefs()).joinToString(",")
		return """{"fileClass":${str(className)},"hasMain":$hasMain,"fields":[${statFields.joinToString(",")}],"methods":[$methods],"types":[$types]}"""
	}

	private fun interfaceDef(iface: IrClass): String {
		val methods = iface.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.correspondingPropertySymbol == null && !it.isFakeOverride }
			.joinToString(",") {
				"""{"name":${str(it.name.asString())},"static":false,"override":false,"virtual":true,"params":[${paramsJson(it.parameters)}],"ret":${str(birType(it.returnType))},"body":[]}"""
			}
		return """{"name":${str(iface.name.asString())},"kind":"interface"${typeParamsJson(iface.typeParameters)},"base":null,"fields":[],"ctors":[],"methods":[$methods]}"""
	}

	/** A Kotlin `enum class` -> a real .NET enum (ilemit DefineEnum + literals). */
	private fun enumDef(e: IrClass): String {
		val entries = e.declarations.filterIsInstance<IrEnumEntry>()
			.mapIndexed { i, ent -> """{"name":${str(ent.name.asString())},"ordinal":$i}""" }
		return """{"name":${str(e.name.asString())},"kind":"enum","entries":[${entries.joinToString(",")}]}"""
	}

	/** A "rich" enum has ctor params, user instance methods, or per-entry bodies -> can't be a CLR enum. */
	private fun isRichEnum(ec: IrClass): Boolean {
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
	private fun richEnumDef(ec: IrClass): String {
		val name = ec.name.asString()
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
		val ctor = """{"params":[$ctorParams],"baseArgs":null,"thisArgs":null,"vis":"private","body":[$ctorBody]}"""
		// instance fields: metadata + user props.
		val fields = (listOf("""{"name":"__name","type":"string"}""", """{"name":"__ordinal","type":"int"}""") + userFields).toMutableList()
		// per-entry static singleton, init = new <Enum>("NAME", ordinal, <entry ctor args>).
		entries.forEachIndexed { i, ent ->
			val cc = (ent.initializerExpression as? IrExpressionBody)?.expression as? IrEnumConstructorCall
			val entryArgs = cc?.let { regularArgs(it).map { a -> expr(a) } }.orEmpty()
			val newArgs = (listOf("""{"k":"const","type":"string","value":${str(ent.name.asString())}}""", """{"k":"const","type":"int","value":$i}""") + entryArgs).joinToString(",")
			fields.add("""{"name":${str(ent.name.asString())},"type":${str("@$name")},"static":true,"init":{"k":"new","type":${str(name)},"args":[$newArgs]}}""")
		}
		// methods: user methods + ToString + values() + valueOf().
		val userMethods = ec.declarations.filterIsInstance<IrSimpleFunction>()
			.filter { it.origin.toString() == "DEFINED" && it.correspondingPropertySymbol == null && it.body != null }
			.map { method(it, static = false) }
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
		return """{"name":${str(name)},"kind":"class","vis":${str(visOf(ec))},"base":null,"interfaces":[],"fields":[${fields.joinToString(",")}],"ctors":[$ctor],"methods":[$methods]}"""
	}

	/** Nested non-inner user classes inside [c] (recursively); excludes companion/inner/anonymous/@Clr. */
	private fun nestedClasses(c: IrClass): List<IrClass> {
		val out = ArrayList<IrClass>()
		c.declarations.filterIsInstance<IrClass>()
			.filter { it.kind == ClassKind.CLASS && !it.isCompanion && !it.isInner && clrName(it) == null && it.name.asString() != "<no name provided>" }
			.forEach { out.add(it); out.addAll(nestedClasses(it)) }
		return out
	}

	/** `inner class`es nested (recursively) inside a class -> flattened to top-level synthetic types. */
	private fun innerClasses(c: IrClass): List<IrClass> {
		val out = ArrayList<IrClass>()
		c.declarations.filterIsInstance<IrClass>()
			.filter { it.kind == ClassKind.CLASS && !it.isCompanion && clrName(it) == null && it.name.asString() != "<no name provided>" }
			.forEach { if (it.isInner) out.add(it); out.addAll(innerClasses(it)) }
		return out
	}

	/** Emit a flattened `inner class`: it captures the enclosing instance as a leading `__outer` ctor param/field. */
	private fun innerClassDef(inner: IrClass): String {
		val outerThis = (inner.parent as? IrClass)?.thisReceiver
			?: return typeDef(inner)   // not actually inner-of-class; emit plainly
		captureSubst[outerThis] = """{"k":"field","ownerType":${str(typeName(inner))},"recv":{"k":"this"},"name":"__outer"}"""
		val def = typeDef(inner, listOf(outerThis to "__outer"))
		captureSubst.remove(outerThis)
		return def
	}

	private fun typeDef(klass: IrClass, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
		val baseType = klass.superTypes
			.firstOrNull { val k = it.classifierOrNull?.owner as? IrClass; k != null && k.kind == ClassKind.CLASS && k.fqNameWhenAvailable?.asString() != "kotlin.Any" }
		val base = baseType?.classifierOrNull?.owner as? IrClass
		val companion = klass.declarations.filterIsInstance<IrClass>().firstOrNull { it.isCompanion }
		val instFields = klass.declarations.filterIsInstance<IrProperty>().mapNotNull { it.backingField }
			.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }
		// Companion non-const `val`/`var` -> static fields (with initializer run in a static ctor); const is inlined.
		val statFields = companion?.declarations?.filterIsInstance<IrProperty>()?.mapNotNull { p ->
			val bf = p.backingField ?: return@mapNotNull null
			if (p.isConst) return@mapNotNull null
			val init = (bf.initializer as? IrExpressionBody)?.expression?.let { expr(it) } ?: "null"
			"""{"name":${str(bf.name.asString())},"type":${str(birType(bf.type))},"static":true,"init":$init}"""
		}.orEmpty()
		// A capturing object literal carries its captured outer values as extra instance fields.
		val capFields = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(birType(decl.type))}}""" }
		val fields = (instFields + statFields + capFields).joinToString(",")
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
		// Property accessors that override a .NET base virtual property -> emitted as get_/set_ override methods.
		val clrAccessors = klass.declarations.filterIsInstance<IrProperty>()
			.flatMap { p -> listOfNotNull(clrAccessorMethod(p, p.getter), clrAccessorMethod(p, p.setter)) }
		val methods = (instMethods + statMethods + clrAccessors).joinToString(",")
		// A .NET base class (`: System.Exception(...)`, incl. a generic `: Collection<Int>()`) -> a `clr:`/`clrg:`
		// type spec (via birType) that ilemit resolves by reflection; a Kotlin-user base stays a bare type name.
		val baseJson = base?.let { if (clrName(it) != null) str(birType(baseType!!)) else str(typeName(it)) } ?: "null"
		// Stdlib interface supertypes (Iterator, Read(Write)Property) -> their monomorphized synthetic interfaces;
		// a user generic interface `Container<Int>` -> the constructed spec `Container[int]` (ownerSpec).
		val ifaces = klass.superTypes
			.filter { (it.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.INTERFACE }
			.mapNotNull { st -> iteratorElemIface(st) ?: propIface(st) ?: (st.classifierOrNull?.owner as? IrClass)?.let { ownerSpec(it, st) } }
			.joinToString(",") { str(it) }
		// Anonymous objects (lifted, tracked in anonNames) are synthetic -> keep public.
		val vis = if (anonNames.containsKey(klass)) "public" else visOf(klass)
		val isAbstract = klass.modality == Modality.ABSTRACT || klass.modality == Modality.SEALED
		return """{"name":${str(typeName(klass))},"kind":"class","abstract":$isAbstract,"vis":${str(vis)}${typeParamsJson(klass.typeParameters)},"base":$baseJson,"interfaces":[$ifaces],"fields":[$fields],"ctors":[$ctors],"methods":[$methods]}"""
	}

	private fun ctor(klass: IrClass, ctor: IrConstructor, captures: List<Pair<IrValueDeclaration, String>> = emptyList()): String {
		// Captured outer values arrive as leading ctor params and are stored into the capture fields first
		// (the instance initializers below read them, e.g. `var cur = from` -> `this.__outer.from`).
		val capParams = captures.map { (decl, fname) -> """{"name":${str(fname)},"type":${str(birType(decl.type))}}""" }
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

	private fun method(fn: IrSimpleFunction, static: Boolean): String {
		if (fn.isSuspend) return suspendMethod(fn, static)
		val isOverride = fn.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.CLASS }
		val isVirtual = fn.modality == Modality.OPEN || fn.modality == Modality.ABSTRACT
		// An extension function `fun T.f()` -> static method whose first param `__self` is the receiver;
		// body references to the receiver resolve to `__self` (via valSubst).
		val extRecv = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		if (extRecv != null) valSubst[extRecv.name.asString()] = """{"k":"local","name":"__self"}"""
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		if (extRecv != null) valSubst.remove(extRecv.name.asString())
		val selfParam = extRecv?.let { """{"name":"__self","type":${str(birType(it.type))}}""" }
		val ps = (listOfNotNull(selfParam) + paramsJsonList(fn.parameters)).joinToString(",")
		// `override fun toString()/equals()/hashCode()` -> System.Object.ToString/Equals/GetHashCode so that
		// CLR virtual dispatch (Console.WriteLine, structural `==`) finds the override.
		val objName = objectMethodName(fn)
		val emitName = objName ?: fn.name.asString()
		val isOvr = isOverride || objName != null
		// Object-overrides / interface members must stay public for virtual dispatch.
		val vis = if (objName != null) "public" else visOf(fn)
		val isAbstract = fn.modality == Modality.ABSTRACT && fn.body == null
		return """{"name":${str(emitName)},"static":$static,"override":$isOvr,"virtual":$isVirtual,"abstract":$isAbstract,"objectOverride":${objName != null},"vis":${str(vis)}${typeParamsJson(fn.typeParameters)},"params":[$ps],"ret":${str(birType(fn.returnType))},"body":[$body]}"""
	}

	// ===== Coroutine (suspend fun) -> CLR-native async state machine (strategy B) =====
	// A `suspend fun f(args): T` lowers to a kickoff `Task<T> f(args)` + a struct IAsyncStateMachine (emitted by
	// ilemit). Here we CPS-linearize the body into a FLAT list of steps so ilemit need not reconstruct control
	// flow: ordinary statements stay as-is (ilemit redirects references to cpsFields onto state-machine fields),
	// suspension points become `coSuspend`, and if/while linearize to `coLabel`/`coGoto`/`coCondGoto`. The CPS
	// logic mirrors the C# D2.1 lowering (CSharpCodegen.emitCps); only the lowered FORM (Task/awaiter vs custom
	// Continuation runtime) differs. See docs/coroutine-il.md. Ported capability bar = C# manual D2.1: linear /
	// loop / branch / direct-suspend-call; try-catch-around-await needs exception regions (E-0.5) -> loud error.
	private var coState = 0
	private var coLabelN = 0
	private var coFields: Set<String> = emptySet()

	// await spilling (D): a nested suspending call -> a fresh state-machine field holding its result, plus a
	// suspension step assigning it. coSpill maps the call node to that field so expr() renders a field reference
	// instead of the call; coSpillFields accumulates (field, type) to declare alongside the params/live-vars.
	private val coSpill = java.util.IdentityHashMap<IrCall, String>()
	private val coSpillFields = ArrayList<Pair<String, IrType>>()

	private fun isAwaitIntrinsic(fn: IrSimpleFunction): Boolean =
		fn.annotations.any { it.type.classFqName?.shortName()?.asString() == "ClrAwait" }

	/** A suspension point: any call to a suspend function (the `.await()` intrinsic or a direct suspend call). */
	private fun isSuspensionCall(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
		e is IrCall && e.symbol.owner.isSuspend

	private fun containsSuspend(e: org.jetbrains.kotlin.ir.IrElement): Boolean {
		var found = false
		e.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				if (found) return
				if (isSuspensionCall(element)) { found = true; return }
				element.acceptChildrenVoid(this)
			}
		})
		return found
	}

	private fun coStmtsOf(e: IrExpression): List<org.jetbrains.kotlin.ir.IrStatement> = when (e) {
		is IrBlock -> e.statements
		is IrComposite -> e.statements
		else -> listOf(e)
	}

	/** Variables declared on any suspension-bearing path -> state-machine fields (survive across resume). */
	private fun collectCpsVars(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, out: MutableList<IrVariable>) {
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
	private fun coAwaitable(call: IrCall): String =
		if (isAwaitIntrinsic(call.symbol.owner)) expr(extensionReceiver(call) ?: dispatchReceiver(call)!!)
		else expr(call)   // a direct suspend call: its kickoff returns Task<T>

	private fun suspendMethod(fn: IrSimpleFunction, static: Boolean): String {
		coState = 0; coLabelN = 0
		coSpill.clear(); coSpillFields.clear()
		val params = regularParams(fn)
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val liveVars = ArrayList<IrVariable>()
		if (fn.body?.let { containsSuspend(it) } == true) collectCpsVars(body, liveVars)
		coFields = (params.map { it.name.asString() } + liveVars.map { it.name.asString() }).toSet()
		val steps = ArrayList<String>()
		for (s in body) emitCps(s, fn.returnType, steps)
		if (body.lastOrNull() !is IrReturn) steps.add("""{"k":"coReturn","value":null}""")
		coFields = emptySet()
		val resultType = if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
		val cpsFields = (params.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" } +
			liveVars.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" } +
			coSpillFields.map { """{"name":${str(it.first)},"type":${str(birType(it.second))}}""" }).joinToString(",")
		val ps = paramsJsonList(fn.parameters).joinToString(",")
		val vis = visOf(fn)
		return """{"name":${str(fn.name.asString())},"static":$static,"override":false,"virtual":false,"objectOverride":false,"vis":${str(vis)},"suspend":true,"resultType":${str(resultType)},"cpsFields":[$cpsFields],"params":[$ps],"steps":[${steps.joinToString(",")}]}"""
	}

	private fun coFresh(): String = "__cor${coLabelN++}"

	private fun emitCps(stmt: org.jetbrains.kotlin.ir.IrElement, ret: IrType, steps: MutableList<String>) {
		when (stmt) {
			is IrVariable -> {
				val init = stmt.initializer
				when {
					init != null && isSuspensionCall(init) -> emitSuspend(init as IrCall, stmt.name.asString(), steps)
					init != null && containsSuspend(init) -> { spillExpr(init, steps); steps.add(stmt(stmt)) }
					else -> steps.add(stmt(stmt))   // sync var; ilemit redirects a cpsField name to a field store
				}
			}
			is IrReturn -> {
				val v = stmt.value
				when {
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
				isSuspensionCall(stmt) -> emitSuspend(stmt, null, steps)
				containsSuspend(stmt) -> { spillExpr(stmt, steps); steps.add("""{"k":"exprStmt","expr":${expr(stmt)}}""") }
				else -> steps.add("""{"k":"exprStmt","expr":${expr(stmt)}}""")
			}
			is IrSetValue -> if (containsSuspend(stmt)) { spillExpr(stmt, steps); steps.add(stmt(stmt)) } else steps.add(stmt(stmt))
			is IrTry -> if (containsSuspend(stmt)) emitTryCps(stmt, ret, steps) else steps.add(stmt(stmt))
			else -> {
				if (stmt is IrExpression && containsSuspend(stmt)) steps.add(coUnsupported("suspension in an unsupported position (${stmt::class.simpleName})"))
				else steps.add(stmt(stmt as? org.jetbrains.kotlin.ir.IrStatement ?: return))
			}
		}
	}

	private fun coReturnJson(ret: IrType, value: String): String =
		if (ret.isUnit()) """{"k":"coReturn","value":null}""" else """{"k":"coReturn","value":$value}"""

	private fun coUnsupported(of: String): String = """{"k":"coUnsupported","of":${str(of)}}"""

	/**
	 * await spilling: hoist every nested suspending sub-call of `e` into its own state-machine field + suspension
	 * step, in left-to-right evaluation order, so the residual `e` (re-rendered via expr(), which consults coSpill)
	 * is suspension-free. Post-order = a call's receiver/args spill before the call itself, so `f(a.await()).await()`
	 * and `a.await() + b.await()` both linearize correctly. Each spilled value lives in a field because another
	 * suspension may follow before the residual reads it (e.g. `a.await() + b.await()` resumes twice).
	 */
	private fun spillExpr(e: org.jetbrains.kotlin.ir.IrElement, steps: MutableList<String>) {
		e.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
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

	/** A suspension point: start the awaitable; if incomplete, save state and return; on resume read the result. */
	private fun emitSuspend(call: IrCall, assignTo: String?, steps: MutableList<String>) {
		val k = ++coState
		steps.add("""{"k":"coSuspend","state":$k,"awaitable":${coAwaitable(call)},"assignTo":${assignTo?.let { str(it) } ?: "null"},"resultType":${str(birType(call.type))}}""")
	}

	private fun emitCpsBlock(e: IrExpression, ret: IrType, steps: MutableList<String>) =
		coStmtsOf(e).forEach { emitCps(it, ret, steps) }

	private fun emitWhenCps(w: IrWhen, ret: IrType, steps: MutableList<String>) {
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
	private fun emitTryCps(t: IrTry, ret: IrType, steps: MutableList<String>) {
		if (t.finallyExpression != null) { steps.add(coUnsupported("finally around a suspension point")); return }
		if (t.catches.any { containsSuspend(it.result) }) { steps.add(coUnsupported("suspension inside a catch clause")); return }
		val tid = coLabelN++
		steps.add("""{"k":"coTryBegin","id":$tid}""")
		emitCpsBlock(t.tryResult, ret, steps)
		for (c in t.catches) {
			val v = c.catchParameter.name.asString()
			steps.add("""{"k":"coCatchBegin","id":$tid,"excType":${str(netType(c.catchParameter.type))},"var":${str(v)}}""")
			emitCpsBlock(c.result, ret, steps)
		}
		steps.add("""{"k":"coTryEnd","id":$tid}""")
	}

	private fun emitWhileCps(loop: IrWhileLoop, ret: IrType, steps: MutableList<String>) {
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
	private fun typeParamsJson(tps: List<org.jetbrains.kotlin.ir.declarations.IrTypeParameter>): String {
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
	private fun ownerSpec(klass: IrClass?, recvType: IrType?): String {
		klass ?: return "?"
		val name = typeName(klass)
		if (klass.typeParameters.isEmpty()) return name
		val args = (recvType as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }
		if (args.isNullOrEmpty() || args.any { it.classifierOrNull is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol })
			return name
		return "$name[${args.joinToString(",") { birType(it) }}]"
	}

	private fun paramsJson(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): String =
		paramsJsonList(params).joinToString(",")

	/**
	 * A property accessor that OVERRIDES a .NET base virtual property (e.g. `override val Message` over
	 * `System.Exception.Message`) -> a `get_<Name>`/`set_<Name>` method that reuses the base virtual slot
	 * (ilemit marks it Virtual + DefineMethodOverride against the .NET getter). Normal Kotlin properties stay
	 * field-modeled; only .NET-overriding accessors with a body need this. Returns null otherwise.
	 */
	private fun clrAccessorMethod(prop: IrProperty, acc: org.jetbrains.kotlin.ir.declarations.IrSimpleFunction?): String? {
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

	private fun paramsJsonList(params: List<org.jetbrains.kotlin.ir.declarations.IrValueParameter>): List<String> =
		params.filter { it.kind == IrParameterKind.Regular }
			.map { """{"name":${str(it.name.asString())},"type":${str(birType(it.type))}}""" }

	private fun stmt(node: org.jetbrains.kotlin.ir.IrElement): String = when (node) {
		is IrVariable -> """{"k":"var","name":${str(node.name.asString())},"type":${str(birType(node.type))},"init":${node.initializer?.let { expr(it) } ?: "null"}}"""
		// `val x by <delegate>` declared INSIDE a function (IrLocalDelegatedProperty): emit the delegate as a
		// local var; its getter/setter calls (`<get-x>`) are rewritten to delegate access in call() (localDelegates).
		is IrLocalDelegatedProperty -> {
			localDelegates[node.getter] = node
			node.setter?.let { localDelegates[it] = node }
			stmt(node.delegate)
		}
		is IrSetValue -> """{"k":"setLocal","name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		is IrSetField -> {
			val ownerClass = node.symbol.owner.parent as? IrClass
			val clr = ownerClass?.let { clrName(it) }
			val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
			if (clr != null)
				"""{"k":"clrPropSet","type":${str(clr)},"name":${str(node.symbol.owner.name.asString())},"static":false,"recv":$recvJson,"value":${expr(node.value)}}"""
			else
				"""{"k":"setField","ownerType":${str(ownerSpec(ownerClass, node.receiver?.type))},"recv":$recvJson,"name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		}
		is IrReturn -> if (node.value.type.isUnit()) """{"k":"return"}""" else """{"k":"return","value":${expr(node.value)}}"""
		// E-0.5: `while`/`do-while` lower to a CFG block (label/brIf/goto) — the natural IL substrate; break/continue
		// inside become `goto` to the loop's break/continue label (incl. `break@outer`, matched by loop identity).
		// `for`/range stays structured (birForLoop) until §5.4; its break/continue fall to the structured nodes.
		is IrWhileLoop -> cfgWhile(node)
		is IrDoWhileLoop -> cfgDoWhile(node)
		// A break/continue targeting a CFG loop (on the stack) -> `goto` its label; otherwise (a structured
		// for-loop target) the structured node, which ilemit's loop stack resolves.
		is IrBreak -> cfgLoopStack.lastOrNull { it.first === node.loop }?.let { """{"k":"goto","id":${it.third}}""" }
			?: """{"k":"break","label":${labelJson(node.label)}}"""
		is IrContinue -> cfgLoopStack.lastOrNull { it.first === node.loop }?.let { """{"k":"goto","id":${it.second}}""" }
			?: """{"k":"continue","label":${labelJson(node.label)}}"""
		is IrWhen -> cfgWhen(node)
		is IrTry -> tryStmt(node)
		is IrThrow -> """{"k":"throw","value":${expr(node.value)}}"""
		// A value coerced to Unit in statement position (e.g. `i++`) -> emit its inner block as statements
		// (otherwise the side effects — the `<unary>` temp + the assignment — would be dropped).
		is IrTypeOperatorCall -> if (node.operator == IrTypeOperator.IMPLICIT_COERCION_TO_UNIT) {
			val arg = node.argument
			if (arg is IrBlock) """{"k":"block","body":[${arg.statements.joinToString(",") { stmt(it) }}]}"""
			else stmt(arg)
		} else """{"k":"exprStmt","expr":${expr(node)}}"""
		// A local (nested) function -> lift it to a file-class static method (captures become leading params).
		is IrSimpleFunction -> { liftLocalFn(node); """{"k":"block","body":[]}""" }
		is IrBlock -> (if (node.origin?.toString() == "FOR_LOOP") birForLoop(node) else null)
			?: """{"k":"block","body":[${node.statements.joinToString(",") { stmt(it) }}]}"""
		// IrComposite: a scope-less statement container (e.g. a desugared loop body) -> a flat block.
		is IrComposite -> """{"k":"block","body":[${node.statements.joinToString(",") { stmt(it) }}]}"""
		is IrExpression -> """{"k":"exprStmt","expr":${expr(node)}}"""
		else -> """{"k":"unsupportedStmt","of":${str(node::class.simpleName ?: "?")}}"""
	}

	/** A loop label (Kotlin `outer@`) as JSON, or null. break/continue target loops by this label. */
	private fun labelJson(label: String?): String = label?.let { str(it) } ?: "null"

	/** A loop body: a block's statements, or a single bare statement (single-statement loop bodies). */
	private fun loopBody(body: IrExpression?): String = when (body) {
		null -> ""
		is IrBlock -> body.statements.joinToString(",") { stmt(it) }
		else -> stmt(body)
	}

	// Active CFG loops: (loop, continueLabelId, breakLabelId). A break/continue is matched to its target by
	// loop reference identity (so `break@outer` resolves), then emitted as `goto` the right label.
	private val cfgLoopStack = ArrayList<Triple<org.jetbrains.kotlin.ir.expressions.IrLoop, Int, Int>>()

	/** `while(c){B}` -> CFG block: `START: if(!c) goto END; B; goto START; END:`. continue->START, break->END. */
	private fun cfgWhile(node: IrWhileLoop): String {
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
	private fun cfgDoWhile(node: IrDoWhileLoop): String {
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
	private fun birForLoop(block: IrBlock): String? {
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
		// `for (x in collection)` -> an IEnumerator loop (reuses forEachInline; binds x to Current).
		if (source != null && isCollectionType(source.type))
			return """{"k":"forEachInline","label":$lbl,"elem":${str(collectionElemType(source.type))},"src":${expr(source)},"var":${str(loopVar.name.asString())},"body":[$body]}"""
		// `for ((k,v) in map)` -> enumerate the Dictionary (yields KeyValuePair<K,V>); the loop var is the entry
		// (`<destruct>`), and the body's `component1()`/`component2()` map to .Key/.Value.
		if (source != null && isMapType(source.type)) {
			val (kt, vt) = mapKV(source.type)
			return """{"k":"forEachInline","label":$lbl,"elem":"clrg:System.Collections.Generic.KeyValuePair[$kt,$vt]","src":${expr(source)},"var":${str(loopVar.name.asString())},"body":[$body]}"""
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

	private fun tryStmt(node: IrTry): String {
		val catches = node.catches.joinToString(",") { c ->
			val p = c.catchParameter
			"""{"excType":${str(netType(p.type))},"var":${str(p.name.asString())},"body":[${bodyStmts(c.result)}]}"""
		}
		val finally = node.finallyExpression?.let { ""","finally":[${bodyStmts(it)}]""" } ?: ""
		return """{"k":"try","type":${str(birType(node.type))},"body":[${bodyStmts(node.tryResult)}],"catches":[$catches]$finally}"""
	}

	private fun bodyStmts(e: IrExpression): String =
		if (e is IrBlock) e.statements.joinToString(",") { stmt(it) } else stmt(e)

	/**
	 * Statement-position `if`/`when` -> CFG branches (E-0.5 §5.3): each non-else branch is
	 * `brIf NEXT (!cond); body; goto END; NEXT:`; the else body falls through to `END:`. Expression-position
	 * if/when keep the value form (`ternary`, via expr). Mixes freely with CFG loops (break/return inside work).
	 */
	private fun cfgWhen(node: IrWhen): String {
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

	private fun expr(node: IrExpression): String {
		// Spilled suspension: a nested `.await()` already hoisted into a state-machine field by spillExpr; the
		// residual expression references that field instead of re-evaluating the suspension. (await spilling, D)
		coSpill[node]?.let { return """{"k":"local","name":${str(it)}}""" }
		return exprInner(node)
	}

	private fun exprInner(node: IrExpression): String = when (node) {
		is IrConst -> """{"k":"const","type":${str(birType(node.type))},"value":${constJson(node)}}"""
		is IrGetValue -> {
			val owner = node.symbol.owner
			val name = owner.name.asString()
			when {
				captureSubst.containsKey(owner) -> captureSubst[owner]!!
				valSubst.containsKey(name) -> valSubst[name]!!
				name == "<this>" -> """{"k":"this"}"""
				else -> """{"k":"local","name":${str(name)}}"""
			}
		}
		is IrGetEnumValue -> {
			val entry = node.symbol.owner
			val parent = entry.parent as? IrClass
			// Rich enum -> the static singleton field; basic enum -> ordinal const typed as the CLR enum.
			if (parent != null && isRichEnum(parent))
				"""{"k":"staticField","ownerType":${str(parent.name.asString())},"name":${str(entry.name.asString())}}"""
			else {
				val ord = parent?.declarations?.filterIsInstance<IrEnumEntry>()?.indexOf(entry) ?: 0
				"""{"k":"enumValue","type":${str("@" + parent?.name?.asString())},"ordinal":$ord}"""
			}
		}
		is IrBlock -> blockExpr(node)
		is IrGetField -> {
			val ownerClass = node.symbol.owner.parent as? IrClass
			val clr = ownerClass?.let { clrName(it) }
			val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
			// A .NET member (e.g. inherited `Exception.Message`) is modeled as a field by the FIR injector but is
			// really a property -> emit a property getter call (`callvirt get_<name>`), not `ldfld`.
			if (clr != null)
				"""{"k":"clrPropGet","type":${str(clr)},"name":${str(node.symbol.owner.name.asString())},"retType":${str(netType(node.type))},"static":false,"recv":$recvJson}"""
			else
				"""{"k":"field","ownerType":${str(ownerSpec(ownerClass, node.receiver?.type))},"recv":$recvJson,"name":${str(node.symbol.owner.name.asString())}}"""
		}
		is IrConstructorCall -> {
			val klass = node.symbol.owner.parent as? IrClass
			// A generic .NET type (`Collection<Int>()`) -> a constructed `clrg:` spec; non-generic stays plain.
			val clr = klass?.let { clrName(it) }?.let { net ->
				val args = (node.type as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
				if (args.isNullOrEmpty()) net else "clrg:$net[${args.joinToString(",")}]"
			}
			// Kotlin builtin exceptions (IllegalStateException etc.) -> their .NET counterpart.
			val netExc = klass?.fqNameWhenAvailable?.asString()?.let { NET_EXCEPTIONS[it] }
			val mapped = clr ?: netExc
			if (mapped != null)
				"""{"k":"clrNew","type":${str(mapped)},"argTypes":[${paramNetTypes(node.symbol.owner)}],"args":[${regularArgs(node).joinToString(",") { expr(it) }}]}"""
			else {
				// An inner-class ctor takes the enclosing instance (its dispatch receiver) as a leading arg.
				val outerArg = if (klass?.isInner == true) dispatchReceiver(node)?.let { expr(it) } else null
				val args = (listOfNotNull(outerArg) + regularArgs(node).map { expr(it) }).joinToString(",")
				"""{"k":"new","type":${str(klass?.let { ownerSpec(it, node.type) } ?: "object")},"args":[$args]}"""
			}
		}
		is IrStringConcatenation -> """{"k":"concat","parts":[${node.arguments.joinToString(",") { expr(it) }}]}"""
		is IrTypeOperatorCall -> when (node.operator) {
			// `x is T` (exhaustive when matching) -> isinst + not-null check.
			IrTypeOperator.INSTANCEOF -> """{"k":"isinst","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
			IrTypeOperator.NOT_INSTANCEOF -> """{"k":"un","op":"!","e":{"k":"isinst","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}}"""
			// `x as T` / smart-cast downcast -> castclass (or unbox for value types). Throws on mismatch.
			IrTypeOperator.CAST, IrTypeOperator.IMPLICIT_CAST ->
				"""{"k":"cast","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
			// `x as? T` -> null on mismatch. Reference T: `isinst T` (null or ref). Value T: `T?` (Nullable<T>).
			IrTypeOperator.SAFE_CAST -> {
				val velem = VALUE_PRIM_BIR[node.typeOperand.classFqName?.asString()]
				if (velem != null) """{"k":"safeCastValue","elem":${str(velem)},"e":${expr(node.argument)}}"""
				else """{"k":"isinstRef","type":${str(birType(node.typeOperand))},"e":${expr(node.argument)}}"""
			}
			// Coercions to Unit / not-null pass the value through.
			else -> expr(node.argument)
		}
		is IrWhen -> ternary(node)
		// `T::class` / `Foo::class` -> a System.Type token. For a generic param `T` this is `ldtoken !!0` in the
		// generic method (CLR reified generics); `Foo::class` is a concrete `ldtoken Foo`.
		is IrClassReference -> """{"k":"classRef","type":${str(birType(node.classType))}}"""
		// `x::class` (runtime class of an instance) -> `x.GetType()` (a System.Type); `.simpleName`/`.qualifiedName`
		// on the result route to Type.Name/FullName, same as the `T::class` literal path.
		is IrGetClass -> """{"k":"getType","e":${expr(node.argument)}}"""
		// `throw` in expression position (e.g. `x ?: throw ...`, `if (c) v else throw ...`): type Nothing,
		// transfers control so no value reaches the surrounding merge point.
		is IrThrow -> throwExpr(expr(node.value))
		is IrCall -> call(node)
		// A property reference passed to a delegate's getValue/setValue -> a `new KPropertyImpl("<name>")`.
		is IrPropertyReference -> {
			needsKProperty = true
			"""{"k":"new","type":"KPropertyImpl","args":[{"k":"const","type":"string","value":${str(node.symbol.owner.name.asString())}}]}"""
		}
		is IrFunctionExpression -> lambda(node)
		// A callable reference `::foo` -> a delegate bound to the referenced function (same Func/Action as a lambda).
		is IrFunctionReference -> functionRef(node)
		// A `vararg` argument -> a newArray. A spread `*a` (IrSpreadElement) passes an existing array: a lone
		// spread is forwarded as-is; all-literal builds a fresh array; mixed `f(1,*a,2)` is a clean deferral.
		is IrVararg -> {
			val spreads = node.elements.filterIsInstance<IrSpreadElement>()
			val directs = node.elements.filterIsInstance<IrExpression>()
			when {
				spreads.size == 1 && directs.isEmpty() -> expr(spreads[0].expression)
				spreads.isEmpty() -> """{"k":"newArray","elem":${str(birType(node.varargElementType))},"elems":[${directs.joinToString(",") { expr(it) }}]}"""
				// `f(1, *a, 2)` -> build a List<elem> (Add literals / AddRange spreads), then ToArray.
				else -> {
					val parts = node.elements.joinToString(",") { e ->
						when (e) {
							is IrSpreadElement -> """{"spread":true,"e":${expr(e.expression)}}"""
							is IrExpression -> """{"spread":false,"e":${expr(e)}}"""
							else -> """{"spread":false,"e":{"k":"const","type":"void","value":null}}"""
						}
					}
					"""{"k":"spreadConcat","elem":${str(birType(node.varargElementType))},"parts":[$parts]}"""
				}
			}
		}
		else -> """{"k":"unsupportedExpr","of":${str(node::class.simpleName ?: "?")}}"""
	}

	/**
	 * A lambda -> a delegate. Non-capturing lambdas lift to a static method (`delegateNew`); capturing
	 * lambdas synthesize a closure class (fields = captured vars, instance `invoke` method) (`closureNew`).
	 */
	private fun lambda(node: IrFunctionExpression): String {
		val fn = node.function
		// A `suspend () -> T` lambda is a coroutine; in the CLR ABI it is a `Func<Task<T>>` (coroutine-abi-decision).
		// The trivial builder lambda `{ f() }` (a single tail suspend call) just returns f()'s kickoff Task, so the
		// emitted body is correct as-is — only the declared return type / delegate type become Task<T> / Func<Task<T>>.
		val ret = if (fn.isSuspend) coTaskType(fn.returnType) else if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
		val ftype = if (fn.isSuspend) coSuspendFuncType(fn) else funcTypeOf(fn)
		// A lambda has no `this` of its own, so a referenced `<this>` is the enclosing instance -> capture it.
		val captures = capturedVars(fn, includeThis = true)
		if (captures.isEmpty()) {
			val lname = "__lambda${lambdaCounter++}"
			val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}""")
			return """{"k":"delegateNew","method":${str(lname)},"funcType":${str(ftype)}}"""
		}
		// Capturing: build a closure class. Captures rewrite to `this.<field>` (by symbol identity, so the
		// enclosing `this` — captured when the lambda reads a member — maps to a `__outer` field, not the
		// closure's own `this`).
		val cname = "__Closure${closureCounter++}"
		val capPairs = captures.map { it to captureFieldName(it) }
		capPairs.forEach { (decl, fname) ->
			captureSubst[decl] = """{"k":"field","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)}}"""
		}
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		capPairs.forEach { (decl, _) -> captureSubst.remove(decl) }
		val fields = capPairs.joinToString(",") { (decl, fname) -> """{"name":${str(fname)},"type":${str(birType(decl.type))}}""" }
		val ctorBody = capPairs.joinToString(",") { (_, fname) -> """{"k":"setField","ownerType":${str(cname)},"recv":{"k":"this"},"name":${str(fname)},"value":{"k":"local","name":${str(fname)}}}""" }
		val invoke = """{"name":"invoke","static":false,"override":false,"virtual":false,"params":[${lambdaParamsJson(fn.parameters)}],"ret":${str(ret)},"body":[$body]}"""
		liftedTypes.add("""{"name":${str(cname)},"kind":"class","base":null,"interfaces":[],"fields":[$fields],"ctors":[{"params":[$fields],"baseArgs":null,"body":[$ctorBody]}],"methods":[$invoke]}""")
		// Capture values are evaluated in the enclosing context (the outer `this`, or an outer local).
		val capExprs = captures.joinToString(",") { capValueExpr(it) }
		return """{"k":"closureNew","closureType":${str(cname)},"captures":[$capExprs],"method":"invoke","funcType":${str(ftype)}}"""
	}

	/**
	 * A callable reference `::foo` -> a delegate bound to the referenced function. v1 scope: a top-level/static
	 * function reference (no receiver, no bound args) reuses the lambda `delegateNew` path — top-level funs are
	 * emitted as static file-class methods, so `FindStatic(name)` resolves the `ldftn` target. Bound-instance
	 * (`obj::method`), member, and constructor references are deferred (clean `unsupportedExpr`).
	 */
	private fun functionRef(node: IrFunctionReference): String {
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
				liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false,"params":[$psJson],"ret":${str(retT)},"body":[{"k":"return","value":$newE}]}""")
				return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${ps.joinToString(",") { birTypeDeleg(it.type) }}"}"""
			}
			return """{"k":"unsupportedExpr","of":"ConstructorReference(non-user)"}"""
		}
		val fn = node.symbol.owner as? IrSimpleFunction
			?: return """{"k":"unsupportedExpr","of":"FunctionReference(non-simple)"}"""
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
			liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false,"params":[$psJson],"ret":${str(retT)},"body":[$body]}""")
			return """{"k":"delegateNew","method":${str(lname)},"funcType":"func:$retT:${(listOf(selfT) + ps.map { birTypeDeleg(it.type) }).joinToString(",")}"}"""
		}
		return """{"k":"unsupportedExpr","of":"FunctionReference(bound/member ${fn.name.asString()})"}"""
	}

	/** The kickoff/return BIR type for a `suspend (...) -> T`: `Task<T>` (or non-generic `Task` for Unit). */
	private fun coTaskType(ret: IrType): String =
		if (ret.isUnit()) "clr:System.Threading.Tasks.Task" else "clrg:System.Threading.Tasks.Task[${birType(ret)}]"

	/** The delegate type for a `suspend (P...) -> T`: `Func<P..., Task<T>>` encoded as `func:<Task<T>>:<P...>`. */
	private fun coSuspendFuncType(fn: IrSimpleFunction): String {
		val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(",") { birTypeDeleg(it.type) }
		return "func:${coTaskType(fn.returnType)}:$ps"
	}

	/**
	 * Inline a scope function `recv.let/run/with/apply/also { ... }` to a value-block: bind the receiver to
	 * a unique local, rewrite `it`/`this` to it, then yield the lambda's last expression (let/run/with) or
	 * the receiver (apply/also). No delegate — the lambda body is spliced in directly.
	 */
	private fun inlineScope(fq: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
		val fn = lambda.function
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

	private fun hasLambdaArg(call: IrCall): Boolean = regularArgs(call).any { it is IrFunctionExpression }

	/** Statements of a function/lambda body (block body, or a single-expression `= expr` body). */
	private fun bodyStatements(body: org.jetbrains.kotlin.ir.IrElement?): List<org.jetbrains.kotlin.ir.IrStatement> = when (body) {
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
	private fun inlineCall(call: IrCall): String {
		val callee = call.symbol.owner
		val params = callee.parameters.filter { it.kind == IrParameterKind.Regular }
		val args = regularArgs(call)
		val pre = ArrayList<String>()
		val boundVals = ArrayList<String>(); val boundLams = ArrayList<org.jetbrains.kotlin.ir.declarations.IrValueDeclaration>()
		for ((p, arg) in params.zip(args)) {
			if (arg is IrFunctionExpression) { inlineLambdas[p] = arg; boundLams.add(p) }
			else {
				val tmp = "__inl${inlCounter++}"
				pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(p.type))},"init":${expr(arg)}}""")
				valSubst[p.name.asString()] = """{"k":"local","name":${str(tmp)}}"""; boundVals.add(p.name.asString())
			}
		}
		val result = spliceBody(bodyStatements(callee.body), callee.returnType.isUnit(), pre)
		boundVals.forEach { valSubst.remove(it) }; boundLams.forEach { inlineLambdas.remove(it) }
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	/** Splice an invoked inlined lambda `f(args)`: bind its params to the invoke args, then splice its body. */
	private fun spliceLambdaCall(lambda: IrFunctionExpression, call: IrCall): String {
		val fn = lambda.function
		val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val args = regularArgs(call)
		val pre = ArrayList<String>(); val bound = ArrayList<String>()
		for ((p, arg) in params.zip(args)) {
			val tmp = "__lam${inlCounter++}"
			pre.add("""{"k":"var","name":${str(tmp)},"type":${str(birType(p.type))},"init":${expr(arg)}}""")
			valSubst[p.name.asString()] = """{"k":"local","name":${str(tmp)}}"""; bound.add(p.name.asString())
		}
		val result = spliceBody(bodyStatements(fn.body), fn.returnType.isUnit() || call.type.isUnit(), pre)
		bound.forEach { valSubst.remove(it) }
		return """{"k":"valueBlock","stmts":[${pre.joinToString(",")}],"result":$result}"""
	}

	/** Emit body statements into `pre`, returning the value expression (Unit -> void const; else the last expr). */
	private fun spliceBody(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, unit: Boolean, pre: MutableList<String>): String {
		if (unit) { stmts.forEach { pre.add(stmt(it)) }; return """{"k":"const","type":"void","value":null}""" }
		stmts.dropLast(1).forEach { pre.add(stmt(it)) }
		return when (val last = stmts.lastOrNull()) {
			is IrReturn -> expr(last.value)
			is IrExpression -> expr(last)
			else -> { last?.let { pre.add(stmt(it)) }; """{"k":"const","type":"void","value":null}""" }
		}
	}

	/** Lift a local function to a file-class static method; captured vars become leading params (by their own names). */
	private fun liftLocalFn(fn: IrSimpleFunction) {
		// Captured vars (incl. the enclosing `this`) become leading params; the call site prepends their values.
		val captures = capturedVars(fn, includeThis = true)
		val lname = "__local${scopeCounter++}_${fn.name.asString()}"
		localFns[fn] = lname to captures
		fun pj(name: String, t: IrType) = """{"name":${str(name)},"type":${str(birType(t))}}"""
		val capPairs = captures.map { it to captureFieldName(it) }
		// A captured `<this>` arrives as an `__outer` param; rewrite `this` refs in the body to that local.
		capPairs.forEach { (decl, fname) -> if (decl.name.asString() == "<this>") captureSubst[decl] = """{"k":"local","name":${str(fname)}}""" }
		val capParams = capPairs.map { (decl, fname) -> pj(fname, decl.type) }
		val ownParams = fn.parameters.filter { it.kind == IrParameterKind.Regular }.map { pj(it.name.asString(), it.type) }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty().joinToString(",") { stmt(it) }
		capPairs.forEach { (decl, _) -> if (decl.name.asString() == "<this>") captureSubst.remove(decl) }
		val ret = if (fn.returnType.isUnit()) "void" else birType(fn.returnType)
		liftedMethods.add("""{"name":${str(lname)},"static":true,"override":false,"virtual":false,"params":[${(capParams + ownParams).joinToString(",")}],"ret":${str(ret)},"body":[$body]}""")
	}

	/** Inline `forEach { it -> body }` into an enumerator loop: bind `it` to a unique loop var, splice the body. */
	private fun inlineForEach(elemT: String, recvExpr: IrExpression, lambda: IrFunctionExpression): String {
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
	private fun collectionElemType(t: IrType): String =
		(t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: "object"

	/** A lambda argument's return BIR type (for inferring LINQ result element types). */
	private fun lambdaRet(arg: IrExpression?): String {
		val fn = (arg as? IrFunctionExpression)?.function
		return if (fn == null || fn.returnType.isUnit()) "void" else birType(fn.returnType)
	}

	/**
	 * Build a generic static call node. `shapes` names the EXACT intended overload's parameter shapes
	 * (ienum/func:N/string/gp/int/…) so ilemit picks it deterministically — no heuristic overload guessing.
	 */
	/** A `new <ExceptionType>(msg?)` node (msgJson is an already-quoted JSON string, or null for the no-arg ctor). */
	private fun newExc(type: String, msgJson: String?): String =
		if (msgJson != null) """{"k":"clrNew","type":${str(type)},"argTypes":["System.String"],"args":[{"k":"const","type":"string","value":$msgJson}]}"""
		else """{"k":"clrNew","type":${str(type)},"argTypes":[],"args":[]}"""

	private fun throwExpr(exc: String): String = """{"k":"throwExpr","value":$exc}"""

	private fun clrGen(type: String, method: String, typeArgs: List<String>, shapes: List<String>, args: List<String>): String =
		"""{"k":"clrGenericStatic","type":${str(type)},"method":${str(method)},"typeArgs":[${typeArgs.joinToString(",") { str(it) }}],"shapes":[${shapes.joinToString(",") { str(it) }}],"args":[${args.joinToString(",")}]}"""

	/** Free value references in a lambda body (referenced but not declared inside) = its captured vars. */
	private fun capturedVars(fn: IrSimpleFunction, includeThis: Boolean = false): List<IrValueDeclaration> {
		val declared = HashSet<IrValueDeclaration>()
		fn.parameters.forEach { declared.add(it) }
		val referenced = LinkedHashSet<IrValueDeclaration>()
		fn.body?.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				when (element) {
					is IrVariable -> declared.add(element)
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
	private fun capturedVarsForObject(anon: IrClass): List<IrValueDeclaration> {
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
	private fun mutatedValues(anon: IrClass): Set<IrValueDeclaration> {
		val out = java.util.Collections.newSetFromMap(java.util.IdentityHashMap<IrValueDeclaration, Boolean>())
		anon.acceptChildrenVoid(object : IrVisitorVoid() {
			override fun visitElement(element: IrElement) {
				if (element is IrSetValue) out.add(element.symbol.owner)
				element.acceptChildrenVoid(this)
			}
		})
		return out
	}

	/** A capture's field name: the enclosing `this` -> `__outer`, an outer local/param -> its own name. */
	private fun captureFieldName(d: IrValueDeclaration): String =
		if (d.name.asString() == "<this>") "__outer" else d.name.asString()

	/** A capture's value at the `new` site (in the enclosing context): the outer `this`, or an outer local. */
	private fun capValueExpr(d: IrValueDeclaration): String =
		if (d.name.asString() == "<this>") """{"k":"this"}""" else """{"k":"local","name":${str(d.name.asString())}}"""

	/** The BIR function-type string `func:<ret>:<arg1>,<arg2>,...` for a lambda's signature. */
	private fun funcTypeOf(fn: IrSimpleFunction): String {
		val ps = fn.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(",") { birTypeDeleg(it.type) }
		val ret = if (fn.returnType.isUnit()) "void" else birTypeDeleg(fn.returnType)
		return "func:$ret:$ps"
	}

	/**
	 * Like `birType`, but erases `KProperty` to `object` for delegate (Func/Action) signatures. A synthetic
	 * type (TypeBuilder) used as a generic argument to a BCL delegate triggers a Reflection.Emit limitation
	 * ("TypeBuilder generic instantiation does not support resolving members"); `Delegates.observable`'s
	 * callback takes a `KProperty` it almost always ignores, so erasing it sidesteps the issue.
	 */
	private fun birTypeDeleg(t: IrType): String {
		val fq = t.classFqName?.asString()
		if (fq != null && (fq.startsWith("kotlin.reflect.KProperty") || fq.startsWith("kotlin.reflect.KMutableProperty"))) return "object"
		return birType(t)
	}

	/** Lambda/closure method params with KProperty erased to object (must agree with funcTypeOf for delegates). */
	private fun lambdaParamsJson(params: List<IrValueParameter>): String =
		params.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { """{"name":${str(it.name.asString())},"type":${str(birTypeDeleg(it.type))}}""" }

	/** Regular args, filling omitted constant default arguments (IL has no default-parameter mechanism). */
	private fun filledArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<String> {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return emptyList()
		val out = ArrayList<String>()
		params.forEachIndexed { i, p ->
			if (p.kind != IrParameterKind.Regular) return@forEachIndexed
			val arg = if (i < call.arguments.size) call.arguments[i] else null
			if (arg != null) out.add(expr(arg))
			else (p.defaultValue?.expression)?.let { out.add(expr(it)) }
		}
		return out
	}

	private fun regularArgs(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): List<IrExpression> {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: emptyList()
		return call.arguments.mapIndexedNotNull { i, a ->
			if (a != null && i < params.size && params[i].kind == IrParameterKind.Regular) a else null
		}
	}

	private fun dispatchReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	/** The callee's ordinary (non-receiver) value parameters, in order. */
	private fun regularParams(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): List<IrValueParameter> =
		callee.parameters.filter { it.kind == IrParameterKind.Regular }

	/**
	 * A parameter-shape token matching ilemit's `Shape(Type)` — used to pick the exact generic-method overload
	 * before `MakeGenericMethod`. A method type parameter is `gp`; primitives/strings/known generics get their
	 * canonical token; everything else is the .NET simple name (`Object`, `Int64`, ...).
	 */
	private fun clrMethodShape(t: IrType): String {
		if (t.classifierOrNull is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol) return "gp"
		if (isArrayType(t)) return "array"
		return when (t.classFqName?.asString()) {
			"kotlin.String" -> "string"
			"kotlin.Char" -> "char"
			"kotlin.Int" -> "int"
			else -> netType(t).substringAfterLast('.')
		}
	}

	private fun extensionReceiver(call: org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	private fun blockExpr(block: IrBlock): String {
		// `object : I { … }` -> a synthetic named class (lifted) + `new`. Instance fields are real fields;
		// captured outer values (incl. the enclosing `this`) become extra ctor params / capture fields.
		if (block.origin?.toString() == "OBJECT_LITERAL") {
			val anon = block.statements.filterIsInstance<IrClass>().firstOrNull()
			if (anon != null) {
				val cname = "__obj${scopeCounter++}"
				anonNames[anon] = cname
				val captured = capturedVarsForObject(anon)
				// Mutable capture (writing an outer local through the object) needs ref cells -> defer cleanly.
				if (captured.any { it in mutatedValues(anon) })
					return """{"k":"unsupportedExpr","of":"capturing-object-literal-mutable"}"""
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
		return (block.statements.lastOrNull() as? IrExpression)?.let { expr(it) } ?: """{"k":"const","type":"void","value":null}"""
	}

	private fun ternary(node: IrWhen): String {
		// Fold right-to-left into nested conditionals.
		var acc = """{"k":"const","type":"void","value":null}"""
		for (b in node.branches.asReversed()) {
			val isElse = (b.condition as? IrConst)?.value == true
			acc = if (isElse) expr(b.result)
			else """{"k":"cond","cond":${expr(b.condition)},"then":${expr(b.result)},"else":$acc}"""
		}
		return acc
	}

	private fun call(call: IrCall): String {
		val callee = call.symbol.owner
		// A `<get-x>`/`<set-x>` call for a LOCAL delegated property -> access on the delegate local (thisRef=null,
		// no enclosing instance). `by lazy`: the local's `.Value`; custom delegate: getValue/setValue(null, KProperty).
		localDelegates[callee]?.let { ldp ->
			val dvar = ldp.delegate
			val dlocal = """{"k":"local","name":${str(dvar.name.asString())}}"""
			val elem = birType(ldp.getter.returnType)
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
				val kprop = """{"k":"new","type":"KPropertyImpl","args":[{"k":"const","type":"string","value":${str(ldp.name.asString())}}]}"""
				val nullRef = """{"k":"const","type":"void","value":null}"""
				return if (callee === ldp.setter)
					"""{"k":"callInstance","ownerType":${str(ownerName)},"virtual":true,"recv":$dlocal,"method":"setValue","args":[$nullRef,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":${str(ownerName)},"virtual":true,"recv":$dlocal,"method":"getValue","args":[$nullRef,$kprop]}"""
			}
		}
		val name = callee.name.asString()
		val declaringClass = callee.parent as? IrClass
		val isBuiltin = declaringClass?.fqNameWhenAvailable?.asString()?.startsWith("kotlin") ?: true
		val pkgFqName = (callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
		val calleeFq = if (declaringClass == null && pkgFqName != null) "$pkgFqName.$name" else null

		// A call to a lifted local function -> static call with captured values (incl. enclosing `this`) prepended.
		localFns[callee]?.let { (lname, caps) ->
			val capArgs = caps.map { capValueExpr(it) }
			return """{"k":"callStatic","owner":null,"method":${str(lname)},"args":[${(capArgs + filledArgs(call)).joinToString(",")}]}"""
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

		// Scope functions (let/run/with/apply/also) -> inline to a value-block (no delegate; mirrors the C# IIFE).
		if (calleeFq in SCOPE_FUNCTIONS) {
			val isWith = calleeFq == "kotlin.with"
			val recvExpr = if (isWith) regularArgs(call).getOrNull(0) else extensionReceiver(call)
			val lambda = (if (isWith) regularArgs(call).getOrNull(1) else regularArgs(call).getOrNull(0)) as? IrFunctionExpression
			if (recvExpr != null && lambda != null) return inlineScope(calleeFq!!, recvExpr, lambda)
		}

		// Collection factories `listOf`/`setOf` -> a `listNew`/`setNew` (List<elem>/HashSet<elem>).
		if (calleeFq in LIST_FACTORIES || calleeFq in SET_FACTORIES) {
			val v = call.arguments.firstOrNull() as? IrVararg
			val elems = v?.elements?.filterIsInstance<IrExpression>().orEmpty()
			val elemT = v?.let { birType(it.varargElementType) } ?: "object"
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
		// Collection ops -> LINQ generic statics (lambdas already lift to delegates). List-producing ops
		// materialize via ToList (Kotlin returns List, not a lazy sequence). Catches both extension ops
		// (map/filter, extension receiver) and member ops (contains, dispatch receiver).
		run {
			val recv = extensionReceiver(call) ?: dispatchReceiver(call)
			val op = name
			if (recv != null && (isCollectionType(recv.type) || isSequenceType(recv.type)) && op in COLLECTION_OPS) {
				val EN = "System.Linq.Enumerable"
				// A `Sequence<T>` receiver is LAZY: intermediate list-producing ops (map/filter/…) stay deferred
				// (no ToList), matching Kotlin's lazy sequence semantics. The explicit `toList`/`toSet` terminals
				// still materialize. (LINQ is already deferred, so this is exactly Kotlin's Sequence behaviour.)
				val lazySeq = isSequenceType(recv.type)
				// Element/result types come from FIR's resolved type arguments (map<T,R>/filter<T>/…);
				// member ops (contains) aren't generic, so fall back to the receiver's element type.
				val targs = call.typeArguments.mapNotNull { it?.let(::birType) }
				val t = targs.getOrNull(0) ?: collectionElemType(recv.type)
				val src = expr(recv)
				val a0 = regularArgs(call).getOrNull(0)
				fun arg() = expr(a0!!)
				// Shapes: ienum=IEnumerable<T> source, func:2=Func<T,_> predicate/selector, gp=generic param value, int.
				val EI = listOf("ienum")                       // (src)
				val EF = listOf("ienum", "func:2")             // (src, predicate/selector)
				// Intermediate list-producing ops: materialize for eager collections, stay deferred for sequences.
				fun toList(inner: String, e: String) = if (lazySeq) inner else clrGen(EN, "ToList", listOf(e), EI, listOf(inner))
				fun any() = if (a0 != null) clrGen(EN, "Any", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "Any", listOf(t), EI, listOf(src))
				return when (op) {
					"map" -> { val tr = targs.getOrNull(1) ?: lambdaRet(a0); toList(clrGen(EN, "Select", listOf(t, tr), EF, listOf(src, arg())), tr) }
					"filter" -> toList(clrGen(EN, "Where", listOf(t), EF, listOf(src, arg())), t)
					"take" -> toList(clrGen(EN, "Take", listOf(t), listOf("ienum", "int"), listOf(src, arg())), t)
					"drop" -> toList(clrGen(EN, "Skip", listOf(t), listOf("ienum", "int"), listOf(src, arg())), t)
					"takeWhile" -> toList(clrGen(EN, "TakeWhile", listOf(t), EF, listOf(src, arg())), t)
					"dropWhile" -> toList(clrGen(EN, "SkipWhile", listOf(t), EF, listOf(src, arg())), t)
					"single" -> if (a0 != null) clrGen(EN, "Single", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "Single", listOf(t), EI, listOf(src))
					"singleOrNull" -> if (a0 != null) clrGen(EN, "SingleOrDefault", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "SingleOrDefault", listOf(t), EI, listOf(src))
					"reversed" -> toList(clrGen(EN, "Reverse", listOf(t), EI, listOf(src)), t)
					"distinct" -> toList(clrGen(EN, "Distinct", listOf(t), EI, listOf(src)), t)
					"toList" -> clrGen(EN, "ToList", listOf(t), EI, listOf(src))
					"toSet" -> clrGen(EN, "ToHashSet", listOf(t), EI, listOf(src))
					// `asSequence()` -> the receiver AS an IEnumerable (LINQ is already lazy); ops on the result
					// route here too (isSequenceType) and stay deferred until a terminal materializes.
					"asSequence" -> src
					"count" -> if (a0 != null) clrGen(EN, "Count", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "Count", listOf(t), EI, listOf(src))
					"any" -> any()
					"none" -> """{"k":"un","op":"!","e":${any()}}"""
					"all" -> clrGen(EN, "All", listOf(t), EF, listOf(src, arg()))
					"first" -> if (a0 != null) clrGen(EN, "First", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "First", listOf(t), EI, listOf(src))
					"last" -> if (a0 != null) clrGen(EN, "Last", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "Last", listOf(t), EI, listOf(src))
					"firstOrNull" -> if (a0 != null) clrGen(EN, "FirstOrDefault", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "FirstOrDefault", listOf(t), EI, listOf(src))
					"lastOrNull" -> if (a0 != null) clrGen(EN, "LastOrDefault", listOf(t), EF, listOf(src, arg())) else clrGen(EN, "LastOrDefault", listOf(t), EI, listOf(src))
					"contains" -> clrGen(EN, "Contains", listOf(t), listOf("ienum", "gp"), listOf(src, arg()))
					"isEmpty" -> """{"k":"un","op":"!","e":${clrGen(EN, "Any", listOf(t), EI, listOf(src))}}"""
					"isNotEmpty" -> clrGen(EN, "Any", listOf(t), EI, listOf(src))
					"sum" -> """{"k":"linqSum","elem":${str(t)},"src":$src}"""
					"sumOf" -> """{"k":"linqSumOf","elem":${str(t)},"selRet":${str(lambdaRet(a0))},"src":$src,"sel":${arg()}}"""
					"sorted" -> toList(clrGen(EN, "Order", listOf(t), EI, listOf(src)), t)
					"sortedDescending" -> toList(clrGen(EN, "OrderDescending", listOf(t), EI, listOf(src)), t)
					"sortedBy" -> toList(clrGen(EN, "OrderBy", listOf(t, lambdaRet(a0)), EF, listOf(src, arg())), t)
					"sortedByDescending" -> toList(clrGen(EN, "OrderByDescending", listOf(t, lambdaRet(a0)), EF, listOf(src, arg())), t)
					"maxOrNull" -> clrGen(EN, "Max", listOf(t), EI, listOf(src))
					"minOrNull" -> clrGen(EN, "Min", listOf(t), EI, listOf(src))
					"maxByOrNull" -> clrGen(EN, "MaxBy", listOf(t, lambdaRet(a0)), EF, listOf(src, arg()))
					"minByOrNull" -> clrGen(EN, "MinBy", listOf(t, lambdaRet(a0)), EF, listOf(src, arg()))
					// associateWith{v}->Dictionary<E,V>; associateBy{k}->Dictionary<K,E>; groupBy{k}->Dictionary<K,List<E>>.
					"associateWith" -> """{"k":"associateWith","keyType":${str(t)},"valType":${str(lambdaRet(a0))},"src":$src,"sel":${arg()}}"""
					"associateBy" -> """{"k":"associateBy","keyType":${str(lambdaRet(a0))},"valType":${str(t)},"src":$src,"sel":${arg()}}"""
					"groupBy" -> """{"k":"groupBy","keyType":${str(lambdaRet(a0))},"elemType":${str(t)},"src":$src,"sel":${arg()}}"""
					// zip(other) -> ToList(Zip<TF,TS>(a, b)) : List<(TF,TS)> (Kotlin Pair -> ValueTuple).
					"zip" -> {
						val other = regularArgs(call)[0]; val ot = collectionElemType(other.type)
						toList(clrGen(EN, "Zip", listOf(t, ot), listOf("ienum", "ienum"), listOf(src, expr(other))), "clrg:System.ValueTuple[$t,$ot]")
					}
					"reduce" -> clrGen(EN, "Aggregate", listOf(t), listOf("ienum", "func:3"), listOf(src, arg()))
					// forEach { it -> body } -> inline body into an enumerator loop (no closure; body uses enclosing locals).
					"forEach" -> {
						val lam = a0 as? IrFunctionExpression
						if (lam != null) inlineForEach(t, recv, lam) else src
					}
					// fold(seed){acc,x->…} -> Enumerable.Aggregate<TSource,TAcc>(src, seed, Func<TAcc,TSource,TAcc>).
					"fold" -> {
						val seed = regularArgs(call)[0]; val lam = regularArgs(call)[1]
						val r = targs.getOrNull(1) ?: birType(seed.type)
						clrGen(EN, "Aggregate", listOf(t, r), listOf("ienum", "gp", "func:3"), listOf(src, expr(seed), expr(lam)))
					}
					// joinToString(sep?) -> String.Join<T>(string sep, IEnumerable<T> src) (default separator ", ").
					"joinToString" -> {
						val sep = regularArgs(call).getOrNull(0)?.let { expr(it) } ?: """{"k":"const","type":"string","value":", "}"""
						clrGen("System.String", "Join", listOf(t), listOf("string", "ienum"), listOf(sep, src))
					}
					else -> src
				}
			}
		}

		// Array factory `intArrayOf(...)`/`arrayOf(...)` -> a `newArray` (vararg elements).
		if (declaringClass == null && name in ARRAY_FACTORY_NAMES &&
			(callee.parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString() == "kotlin") {
			val v = call.arguments.firstOrNull() as? IrVararg
			val elems = v?.elements?.filterIsInstance<IrExpression>().orEmpty()
			val elemT = v?.let { birType(it.varargElementType) } ?: "object"
			return """{"k":"newArray","elem":${str(elemT)},"elems":[${elems.joinToString(",") { expr(it) }}]}"""
		}
		// `e!!` (not-null assertion) -> the value itself (the use site throws on null anyway).
		if (name == "CHECK_NOT_NULL") return expr(call.arguments.filterNotNull().first())

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

		// `a to b` -> a ValueTuple. `pair.componentN()` (destructuring) -> the ItemN field.
		if (calleeFq == "kotlin.to") {
			val a = extensionReceiver(call); val b = regularArgs(call).getOrNull(0)
			if (a != null && b != null)
				return """{"k":"tupleNew","elems":[${str(birType(a.type))},${str(birType(b.type))}],"args":[${expr(a)},${expr(b)}]}"""
		}
		if ((declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.Pair" || declaringClass?.fqNameWhenAvailable?.asString() == "kotlin.Triple")
			&& name.startsWith("component") && name.drop("component".length).all { it.isDigit() }) {
			dispatchReceiver(call)?.let { r ->
				return """{"k":"tupleItem","tupleType":${str(birType(r.type))},"index":${name.removePrefix("component")},"recv":${expr(r)}}"""
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
			// List indexing `list[i]` / `list[i] = v` -> List get_Item / set_Item.
			if (recv != null && isCollectionType(recv.type) && !isSetType(recv.type)) {
				val elemT = collectionElemType(recv.type); val a = regularArgs(call)
				return if (name == "get") """{"k":"listGet","elem":${str(elemT)},"list":${expr(recv)},"index":${expr(a[0])}}"""
				else """{"k":"listSet","elem":${str(elemT)},"list":${expr(recv)},"index":${expr(a[0])},"value":${expr(a[1])}}"""
			}
			// Map indexing `m[k]` / `m[k] = v` -> Dictionary get_Item / set_Item.
			if (recv != null && isMapType(recv.type)) {
				val (kt, vt) = mapKV(recv.type); val a = regularArgs(call)
				return if (name == "get") """{"k":"mapGet","keyType":${str(kt)},"valType":${str(vt)},"map":${expr(recv)},"key":${expr(a[0])}}"""
				else """{"k":"mapSet","keyType":${str(kt)},"valType":${str(vt)},"map":${expr(recv)},"key":${expr(a[0])},"value":${expr(a[1])}}"""
			}
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
			clrc.ClrEventRegistry.lookup(declFq, name)?.let { (eventName, op) ->
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
					val shapes = regularParams(callee).joinToString(",") { str(clrMethodShape(it.type)) }
					val member = clrName(callee) ?: name
					val argsJson = regularArgs(call).joinToString(",") { expr(it) }
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
				return if (callee === prop.setter)
					"""{"k":"clrPropSet","type":${str(memberType)},"name":${str(pn)},"static":$isStatic,"recv":$recvJson,"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"clrPropGet","type":${str(memberType)},"name":${str(pn)},"retType":${str(netType(callee.returnType))},"static":$isStatic,"recv":$recvJson}"""
			}
			val member = clrName(callee) ?: name
			val argsJson = regularArgs(call).joinToString(",") { expr(it) }
			val ret = str(netType(callee.returnType))
			return if (isStatic)
				"""{"k":"clrStatic","type":${str(clrType)},"method":${str(member)},"argTypes":[${paramNetTypes(callee)}],"ret":$ret,"args":[$argsJson]}"""
			else
				"""{"k":"clrInstance","type":${str(memberType)},"method":${str(member)},"argTypes":[${paramNetTypes(callee)}],"ret":$ret,"recv":${expr(recv!!)},"args":[$argsJson]}"""
		}

		// Companion-object member -> a static member of the enclosing class (precedes user-property field access).
		(callee.parent as? IrClass)?.takeIf { it.isCompanion }?.let { comp ->
			val enclosing = (comp.parent as IrClass).name.asString()
			val prop = callee.correspondingPropertySymbol?.owner
			if (prop != null) return if (callee === prop.setter)
				"""{"k":"staticFieldSet","ownerType":${str(enclosing)},"name":${str(prop.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			else """{"k":"staticField","ownerType":${str(enclosing)},"name":${str(prop.name.asString())}}"""
			return """{"k":"callStatic","owner":${str(enclosing)},"method":${str(name)},"args":[${filledArgs(call).joinToString(",")}]}"""
		}

		// Top-level property (parent is the file/package, not a class) -> a static field of the file class.
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			if (declaringClass == null) return if (callee === p.setter)
				"""{"k":"staticFieldSet","ownerType":${str(fileClass)},"name":${str(p.name.asString())},"value":${expr(regularArgs(call).first())}}"""
			else """{"k":"staticField","ownerType":${str(fileClass)},"name":${str(p.name.asString())}}"""
		}

		// `s.length` on a String -> System.String.Length (CLR property).
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			if (p.name.asString() == "length" && dispatchReceiver(call)?.type?.classFqName?.asString() == "kotlin.String")
				return """{"k":"clrPropGet","type":"System.String","name":"Length","retType":"System.Int32","static":false,"recv":${expr(dispatchReceiver(call)!!)}}"""
		}
		// Pair/Triple `.first`/`.second`/`.third` -> ValueTuple ItemN field.
		(callee.correspondingPropertySymbol?.owner)?.let { p ->
			val pfq = (callee.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			if (pfq == "kotlin.Pair" || pfq == "kotlin.Triple") {
				val idx = when (p.name.asString()) { "first" -> 1; "second" -> 2; "third" -> 3; else -> 0 }
				if (idx > 0) dispatchReceiver(call)?.let { r ->
					return """{"k":"tupleItem","tupleType":${str(birType(r.type))},"index":$idx,"recv":${expr(r)}}"""
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
			if (isCollectionType(r.type)) return clrGen("System.Linq.Enumerable", "Count", listOf(collectionElemType(r.type)), listOf("ienum"), listOf(expr(r)))
			if (isMapType(r.type)) { val (kt, vt) = mapKV(r.type); return """{"k":"mapSize","keyType":${str(kt)},"valType":${str(vt)},"map":${expr(r)}}""" }
		}
		// `kProperty.name` -> the compiler-generated KProperty.get_name().
		if (property?.name?.asString() == "name" &&
			declaringClass?.fqNameWhenAvailable?.asString()?.startsWith("kotlin.reflect.KProperty") == true) {
			needsKProperty = true
			val recv = dispatchReceiver(call)?.let { expr(it) } ?: """{"k":"this"}"""
			return """{"k":"callInstance","ownerType":"KProperty","virtual":true,"recv":$recv,"method":"get_name","args":[]}"""
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
			// `val x by map` / `var x by mutableMap` -> `map["x"]` (read, cast to the property type) / `map["x"] = v`.
			if (bf != null && isMapType(bf.type) && delegate != null) {
				val (kt, vt) = mapKV(bf.type)
				val key = """{"k":"const","type":"string","value":${str(property.name.asString())}}"""
				return if (callee === property.setter)
					"""{"k":"mapSet","keyType":${str(kt)},"valType":${str(vt)},"map":$delegate,"key":$key,"value":${expr(regularArgs(call).first())}}"""
				else """{"k":"cast","type":${str(birType(callee.returnType))},"e":{"k":"mapGet","keyType":${str(kt)},"valType":${str(vt)},"map":$delegate,"key":$key}}"""
			}
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
				val kprop = """{"k":"new","type":"KPropertyImpl","args":[{"k":"const","type":"string","value":${str(property.name.asString())}}]}"""
				// callvirt: getValue/setValue is virtual (interface impl) or final (duck-typed) — callvirt fits both.
				return if (callee === property.setter)
					"""{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"setValue","args":[$recv,$kprop,${expr(regularArgs(call).first())}]}"""
				else """{"k":"callInstance","ownerType":$owner,"virtual":true,"recv":$delegate,"method":"getValue","args":[$recv,$kprop]}"""
			}
			return """{"k":"unsupportedExpr","of":"delegated-property-non-lazy"}"""
		}
		if (property != null && declaringClass != null) {
			val recvExpr = dispatchReceiver(call)
			val recv = recvExpr?.let { expr(it) } ?: """{"k":"this"}"""
			val ownerStr = ownerSpec(declaringClass, recvExpr?.type)
			val owner = str(ownerStr)
			return if (callee === property.setter)
				"""{"k":"setFieldExpr","ownerType":$owner,"recv":$recv,"name":${str(property.name.asString())},"value":${expr(regularArgs(call).first())}}"""
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
			BINARY[name]?.let { if (operands.size == 2) return """{"k":"bin","op":${str(it)},"l":${expr(operands[0])},"r":${expr(operands[1])}}""" }
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

		// Fill omitted constant default arguments at the call site (IL methods have no default mechanism).
		val args = filledArgs(call).joinToString(",")
		// A generic method `fun <T> id(...)` -> carry the resolved type args so ilemit can MakeGenericMethod.
		val ta = typeArgsJson(call)
		val recv = dispatchReceiver(call)
		// User extension function `fun T.f(...)` -> static `f(receiver, args...)` (receiver is the __self param).
		val extRecv = extensionReceiver(call)
		if (extRecv != null) {
			val all = (listOf(expr(extRecv)) + filledArgs(call)).joinToString(",")
			return """{"k":"callStatic","owner":null,"method":${str(name)}$ta${retHint(ta.isNotEmpty(), call.type)},"args":[$all]}"""
		}
		// Instance method on a user class, or a sibling top-level call.
		return if (recv != null) {
			// `it.hasNext()`/`it.next()` on a Kotlin iterator -> dispatch on the monomorphized synthetic interface.
			iteratorElemIface(recv.type)?.let { ifaceName ->
				return """{"k":"callInstance","ownerType":${str(ifaceName)},"virtual":true,"recv":${expr(recv)},"method":${str(name)},"args":[$args]}"""
			}
			val ownerStr = ownerSpec(declaringClass, recv.type)
			val virtual = callee.modality != Modality.FINAL || callee.overriddenSymbols.isNotEmpty()
			val mname = objectMethodName(callee) ?: name
			"""{"k":"callInstance","ownerType":${str(ownerStr)},"virtual":$virtual,"recv":${expr(recv)},"method":${str(mname)}$ta${retHint(ta.isNotEmpty() || '[' in ownerStr, call.type)},"args":[$args]}"""
		} else """{"k":"callStatic","owner":null,"method":${str(name)}$ta${retHint(ta.isNotEmpty(), call.type)},"args":[$args]}"""
	}

	/**
	 * `,"retType":"int"` for a generic call/member access: the concrete result type is known here (FIR-resolved
	 * `call.type`), so ilemit need not reflect the un-baked builder's return type (which stays `!0`/`!!0` and
	 * would mis-drive value-type boxing). Only emitted for the generic/constructed paths to stay non-invasive.
	 */
	private fun retHint(generic: Boolean, t: IrType): String =
		if (generic) ""","retType":${str(birType(t))}""" else ""

	/** `,"typeArgs":["int"]` when the callee is a generic method (its own type params resolved at this call). */
	private fun typeArgsJson(call: IrCall): String {
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
	private fun clrName(decl: org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer): String? {
		for (a in decl.annotations) {
			if ((a as? IrConstructorCall)?.type?.classFqName?.asString() == "clr.Clr")
				return (a.arguments.firstOrNull() as? IrConst)?.value as? String
		}
		return (decl as? IrClass)?.fqNameWhenAvailable?.asString()?.let { clrc.ClrTypeRegistry.dotNetName(it) }
	}

	/** A type's fully-qualified .NET name, for IL reflection-based member resolution. */
	private fun netType(t: IrType): String = when (val fq = t.classFqName?.asString()) {
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
		else -> NET_EXCEPTIONS[fq]
			?: (t.classifierOrNull?.owner as? IrClass)?.let { clrName(it) }
			?: "System.Object"
	}

	private fun paramNetTypes(callee: org.jetbrains.kotlin.ir.declarations.IrFunction): String =
		callee.parameters.filter { it.kind == IrParameterKind.Regular }
			.joinToString(",") { str(netType(it.type)) }

	private fun constJson(c: IrConst): String = when (val v = c.value) {
		is String -> str(v)
		is Boolean -> v.toString()
		is Char -> str(v.toString())
		null -> "null"
		else -> v.toString()
	}

	/** Kotlin `Array<T>` / primitive arrays -> a BIR `array:<elem>` type (ilemit -> `T[]`). */
	private fun isArrayType(t: IrType): Boolean {
		val fq = t.classFqName?.asString()
		return fq == "kotlin.Array" || fq in PRIMITIVE_ARRAY_ELEM
	}

	private fun arrayElemType(t: IrType): String {
		val fq = t.classFqName?.asString()
		PRIMITIVE_ARRAY_ELEM[fq]?.let { return it }
		if (fq == "kotlin.Array")
			return (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type?.let(::birType) } ?: "object"
		return "object"
	}

	/** Kotlin List/MutableList/Collection/Iterable -> a .NET `List<T>` (works as IEnumerable<T> for LINQ). */
	private fun isCollectionType(t: IrType): Boolean =
		t.classFqName?.asString() in setOf(
			"kotlin.collections.List", "kotlin.collections.MutableList",
			"kotlin.collections.Collection", "kotlin.collections.Iterable",
			"kotlin.collections.Set", "kotlin.collections.MutableSet",
		)

	/** A Kotlin `Sequence<T>` -> a LAZY .NET `IEnumerable<T>` (deferred LINQ). Distinct from collections, whose
	 *  intermediate ops materialize via ToList (Kotlin lists are eager); sequence ops stay deferred. */
	private fun isSequenceType(t: IrType): Boolean =
		t.classFqName?.asString() == "kotlin.sequences.Sequence"

	/** A Kotlin Set type -> .NET HashSet<T>; List-family -> List<T>. */
	private fun isSetType(t: IrType): Boolean =
		t.classFqName?.asString() in setOf("kotlin.collections.Set", "kotlin.collections.MutableSet")

	private fun isMapType(t: IrType): Boolean =
		t.classFqName?.asString() in setOf("kotlin.collections.Map", "kotlin.collections.MutableMap")

	/** (keyType, valType) BIR types of a Map<K,V>. */
	private fun mapKV(t: IrType): Pair<String, String> {
		val a = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
		return (a.getOrNull(0) ?: "object") to (a.getOrNull(1) ?: "object")
	}

	/** Kotlin nullable VALUE type (`Int?`/`Double?`…) -> the BIR element type (int/double…), else null. */
	private fun nullableElem(t: IrType): String? =
		if (t.isMarkedNullable()) VALUE_PRIM_BIR[t.classFqName?.asString()] else null

	/** Kotlin visibility -> BIR access keyword (public/private/internal/protected). */
	private fun visOf(d: IrDeclarationWithVisibility): String = when (d.visibility.delegate) {
		Visibilities.Private, Visibilities.PrivateToThis -> "private"
		Visibilities.Internal -> "internal"
		Visibilities.Protected -> "protected"
		else -> "public"
	}

	/** Non-nullable primitive whose `==` is CIL `ceq` (else `==` is structural `Object.Equals`). */
	private fun isPrimitiveEqType(t: IrType): Boolean =
		!t.isMarkedNullable() && t.classFqName?.asString() in PRIMITIVE_EQ_FQ

	/** A Kotlin `Any`-override -> its System.Object method name (`toString`->`ToString`…), else null. */
	private fun objectMethodName(fn: IrSimpleFunction): String? {
		val reg = fn.parameters.count { it.kind == IrParameterKind.Regular }
		return when (fn.name.asString()) {
			"toString" -> if (reg == 0) "ToString" else null
			"hashCode" -> if (reg == 0) "GetHashCode" else null
			"equals" -> if (reg == 1) "Equals" else null
			else -> null
		}
	}

	private fun birType(t: IrType): String {
		// A type parameter `T` is a real generic parameter -> `gp:<name>` (resolved in IL context). On the CLR,
		// generics are reified, so even `reified T` rides on this (no inlining) — see [[clr-not-jvm-discard-jvmisms]].
		(t.classifierOrNull as? org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol)?.let { tp ->
			return "gp:" + tp.owner.name.asString()
		}
		// Nullable value type `Int?` -> System.Nullable<int> (reference nullables stay as the ref type).
		nullableElem(t)?.let { return "nullable:$it" }
		if (isArrayType(t)) return "array:" + arrayElemType(t)
		// Generic .NET types use a bracket encoding `clrg:Open[arg1,arg2]` so nested generics (List<(A,B)>) parse.
		if (isSetType(t)) return "clrg:System.Collections.Generic.HashSet[" + collectionElemType(t) + "]"
		if (isCollectionType(t)) return "clrg:System.Collections.Generic.List[" + collectionElemType(t) + "]"
		if (isMapType(t)) { val (k, v) = mapKV(t); return "clrg:System.Collections.Generic.Dictionary[$k,$v]" }
		// `Map.Entry<K,V>` (the element of `for ((k,v) in map)`) -> a .NET `KeyValuePair<K,V>`.
		if (t.classFqName?.asString() in setOf("kotlin.collections.Map.Entry", "kotlin.collections.MutableMap.MutableEntry")) {
			val (k, v) = mapKV(t); return "clrg:System.Collections.Generic.KeyValuePair[$k,$v]"
		}
		// Pair<A,B>/Triple<A,B,C> -> System.ValueTuple<...>.
		val fqp = t.classFqName?.asString()
		if (fqp == "kotlin.Pair" || fqp == "kotlin.Triple") {
			val args = (t as? IrSimpleType)?.arguments.orEmpty().mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			return "clrg:System.ValueTuple[" + args.joinToString(",") + "]"
		}
		// kotlin.Comparable<T> -> System.IComparable<T> (bound for `<T : Comparable<T>>`, and `a.compareTo(b)`).
		if (fqp == "kotlin.Comparable") {
			val arg = (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			return "clrg:System.IComparable[$arg]"
		}
		// `by lazy` delegate: kotlin.Lazy<T> -> System.Lazy<T>.
		if (fqp == "kotlin.Lazy") {
			val elem = (t as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }?.let(::birType) ?: "object"
			return "clrg:System.Lazy[$elem]"
		}
		// kotlin.reflect.KProperty* (delegated-property metadata) -> the synthetic compiler-generated `KProperty`.
		if (fqp != null && (fqp.startsWith("kotlin.reflect.KProperty") || fqp.startsWith("kotlin.reflect.KMutableProperty"))) {
			needsKProperty = true; return "@KProperty"
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
		// The Kotlin iterator protocol type -> a monomorphized synthetic interface (`@KIterator_<elem>`).
		iteratorElemIface(t)?.let { return "@$it" }
		val klass = t.classifierOrNull?.owner as? IrClass
		// A @Clr / FIR-injected .NET type ("clr:System.Text.StringBuilder"); a constructed generic .NET type
		// (`Collection<Int>`) carries its concrete args as `clrg:<openName>[int]`.
		klass?.let { clrName(it) }?.let { netName ->
			val args = (t as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type?.let(::birType) }
			return if (args.isNullOrEmpty()) "clr:$netName" else "clrg:$netName[${args.joinToString(",")}]"
		}
		// Enums -> the real .NET enum type reference.
		if (klass != null && klass.kind == ClassKind.ENUM_CLASS) return "@" + klass.name.asString()
		// A user-declared class/interface becomes a reference to that BIR type ("@Name"); a constructed user
		// generic carries concrete args ("@Box[int]"). Anon objects resolve through `typeName`.
		if (klass != null && (klass.kind == ClassKind.CLASS || klass.kind == ClassKind.INTERFACE)) {
			if (klass.typeParameters.isNotEmpty()) {
				val args = (t as? IrSimpleType)?.arguments?.mapNotNull { (it as? IrTypeProjection)?.type }
				if (!args.isNullOrEmpty() && args.none { it.classifierOrNull is org.jetbrains.kotlin.ir.symbols.IrTypeParameterSymbol })
					return "@" + typeName(klass) + "[" + args.joinToString(",") { birType(it) } + "]"
			}
			return "@" + typeName(klass)
		}
		return "object"
	}

	private fun str(s: String): String =
		"\"" + s.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n").replace("\t", "\\t") + "\""

	companion object {
		private val BINARY = mapOf(
			"plus" to "+", "minus" to "-", "times" to "*", "div" to "/", "rem" to "%",
			"less" to "<", "lessOrEqual" to "<=", "greater" to ">", "greaterOrEqual" to ">=",
			"EQEQ" to "==", "EQEQEQ" to "==",
			// Bitwise / shift infix functions (Int/Long/Boolean).
			"and" to "&", "or" to "|", "xor" to "^", "shl" to "<<", "shr" to ">>", "ushr" to ">>>",
		)
		private val UNARY = mapOf("unaryMinus" to "-", "unaryPlus" to "+", "not" to "!", "inv" to "~")

		// kotlin.math.* -> System.Math.* (ilemit picks the int/double overload by argTypes).
		private val MATH_FUNCS = mapOf(
			"abs" to "Abs", "max" to "Max", "min" to "Min", "sqrt" to "Sqrt", "pow" to "Pow",
			"round" to "Round", "floor" to "Floor", "ceil" to "Ceiling", "exp" to "Exp",
			"ln" to "Log", "log10" to "Log10", "sin" to "Sin", "cos" to "Cos", "tan" to "Tan",
		)

		// kotlin.text String ops -> .NET System.String instance methods.
		private val STRING_OPS = mapOf(
			"uppercase" to "ToUpper", "lowercase" to "ToLower", "trim" to "Trim",
			"trimStart" to "TrimStart", "trimEnd" to "TrimEnd", "substring" to "Substring",
			"replace" to "Replace", "startsWith" to "StartsWith", "endsWith" to "EndsWith",
			"contains" to "Contains", "indexOf" to "IndexOf", "padStart" to "PadLeft", "padEnd" to "PadRight",
		)

		// `"42".toInt()` etc. -> a static `Parse` on the target .NET numeric type.
		private val NUMBER_PARSE = mapOf(
			"toInt" to "System.Int32", "toLong" to "System.Int64", "toDouble" to "System.Double",
			"toFloat" to "System.Single", "toShort" to "System.Int16", "toByte" to "System.Byte",
		)
		// Char predicates / conversions -> static methods on System.Char.
		private val CHAR_OPS = mapOf(
			"isDigit" to "IsDigit", "isLetter" to "IsLetter", "isWhitespace" to "IsWhiteSpace",
			"isLetterOrDigit" to "IsLetterOrDigit", "uppercaseChar" to "ToUpper", "lowercaseChar" to "ToLower",
			"isUpperCase" to "IsUpper", "isLowerCase" to "IsLower",
		)

		private val PRIMITIVE_ARRAY_ELEM = mapOf(
			"kotlin.IntArray" to "int", "kotlin.LongArray" to "long", "kotlin.DoubleArray" to "double",
			"kotlin.FloatArray" to "float", "kotlin.BooleanArray" to "bool", "kotlin.CharArray" to "char",
			"kotlin.ByteArray" to "byte", "kotlin.ShortArray" to "short",
		)
		private val ARRAY_FACTORY_NAMES = setOf(
			"arrayOf", "intArrayOf", "longArrayOf", "doubleArrayOf",
			"floatArrayOf", "booleanArrayOf", "charArrayOf", "byteArrayOf", "shortArrayOf",
		)
		private val LIST_FACTORIES = setOf(
			"kotlin.collections.listOf", "kotlin.collections.mutableListOf", "kotlin.collections.arrayListOf",
		)
		private val SET_FACTORIES = setOf(
			"kotlin.collections.setOf", "kotlin.collections.mutableSetOf", "kotlin.collections.hashSetOf",
		)
		private val MAP_FACTORIES = setOf(
			"kotlin.collections.mapOf", "kotlin.collections.mutableMapOf", "kotlin.collections.hashMapOf",
		)
		private val COLLECTION_OPS = setOf(
			"map", "filter", "take", "drop", "reversed", "distinct", "toList",
			"count", "any", "none", "all", "first", "last", "contains", "fold", "joinToString", "forEach",
			"firstOrNull", "lastOrNull", "isEmpty", "isNotEmpty", "sum", "sumOf", "sorted", "maxOrNull", "minOrNull", "reduce",
			"maxByOrNull", "minByOrNull", "zip", "associateWith", "associateBy", "groupBy",
			"asSequence", "toSet", "takeWhile", "dropWhile", "single", "singleOrNull",
			"sortedDescending", "sortedBy", "sortedByDescending",
		)

		// Numeric conversions on a number receiver (`3.7.toInt()`) -> a CIL conv to this BIR type.
		private val NUMBER_CONV = mapOf(
			"toInt" to "int", "toLong" to "long", "toDouble" to "double", "toFloat" to "float",
			"toShort" to "short", "toByte" to "byte", "toChar" to "char",
		)
		private val NUMERIC_FQ = setOf(
			"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
			"kotlin.Double", "kotlin.Float", "kotlin.Char",
		)
		// Value-type primitives -> BIR element type (for Nullable<T> representation of `T?`).
		private val PRIMITIVE_EQ_FQ = setOf(
			"kotlin.Int", "kotlin.Long", "kotlin.Short", "kotlin.Byte",
			"kotlin.Double", "kotlin.Float", "kotlin.Boolean", "kotlin.Char",
		)

		private val VALUE_PRIM_BIR = mapOf(
			"kotlin.Int" to "int", "kotlin.Long" to "long", "kotlin.Short" to "short", "kotlin.Byte" to "byte",
			"kotlin.Double" to "double", "kotlin.Float" to "float", "kotlin.Boolean" to "bool", "kotlin.Char" to "char",
		)

		private val NET_EXCEPTIONS = mapOf(
			"java.lang.Throwable" to "System.Exception", "kotlin.Throwable" to "System.Exception",
			"java.lang.Exception" to "System.Exception", "kotlin.Exception" to "System.Exception",
			"java.lang.RuntimeException" to "System.Exception", "kotlin.RuntimeException" to "System.Exception",
			"java.lang.ArithmeticException" to "System.ArithmeticException",
			"java.lang.IllegalArgumentException" to "System.ArgumentException",
			"java.lang.IllegalStateException" to "System.InvalidOperationException",
			"java.lang.IndexOutOfBoundsException" to "System.IndexOutOfRangeException",
			"java.lang.NullPointerException" to "System.NullReferenceException",
		)
	}
}
