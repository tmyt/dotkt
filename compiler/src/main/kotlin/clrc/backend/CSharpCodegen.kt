package clrc.backend

import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrAnnotationContainer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrEnumEntry
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrPackageFragment
import org.jetbrains.kotlin.ir.declarations.IrParameterKind
import org.jetbrains.kotlin.ir.declarations.IrFunction
import org.jetbrains.kotlin.ir.declarations.IrProperty
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.declarations.IrValueParameter
import org.jetbrains.kotlin.ir.declarations.IrVariable
import org.jetbrains.kotlin.ir.expressions.IrBlock
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConst
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrDelegatingConstructorCall
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrExpressionBody
import org.jetbrains.kotlin.ir.expressions.IrFunctionAccessExpression
import org.jetbrains.kotlin.ir.expressions.IrFunctionExpression
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
import org.jetbrains.kotlin.ir.expressions.IrTypeOperator
import org.jetbrains.kotlin.ir.expressions.IrTypeOperatorCall
import org.jetbrains.kotlin.ir.expressions.IrVararg
import org.jetbrains.kotlin.ir.expressions.IrComposite
import org.jetbrains.kotlin.ir.expressions.IrWhen
import org.jetbrains.kotlin.ir.expressions.IrBreak
import org.jetbrains.kotlin.ir.expressions.IrContinue
import org.jetbrains.kotlin.ir.expressions.IrDoWhileLoop
import org.jetbrains.kotlin.ir.expressions.IrWhileLoop
import org.jetbrains.kotlin.ir.visitors.IrVisitorVoid
import org.jetbrains.kotlin.ir.visitors.acceptChildrenVoid
import org.jetbrains.kotlin.ir.visitors.acceptVoid
import org.jetbrains.kotlin.ir.types.IrSimpleType
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.ir.types.IrTypeProjection
import org.jetbrains.kotlin.ir.types.classFqName
import org.jetbrains.kotlin.ir.types.classifierOrNull
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.types.isUnit
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI

/**
 * M0 codegen: walk raw Kotlin IR for one file and emit a single C# static class.
 *
 * Scope (deliberately small): top-level functions, primitive types, arithmetic/comparison,
 * if/when-as-expression, while, calls, string templates, and `println`. Everything is rendered
 * straight from the IR — no intermediate lowering — because the M0 subset maps 1:1 onto C#.
 *
 * `symbol.owner` access is safe here: we run after IR construction is fully complete.
 */
@OptIn(UnsafeDuringIrConstructionAPI::class)
class CSharpCodegen {

	private val sb = StringBuilder()
	private var indent = 0

	// Inline substitutions for synthetic temporaries (e.g. a `when` subject) used in expression position.
	private val valSubst = HashMap<String, String>()

	// The current file's static class name (so coroutine state machines can call sibling bridges).
	private var fileClass = ""

	private fun line(text: String) {
		repeat(indent) { sb.append("    ") }
		sb.append(text).append('\n')
	}

	fun generateFile(file: IrFile): String {
		sb.setLength(0)
		indent = 0
		fileClass = fileClassName(file)
		// User-declared types (not @Clr façades, not annotations) become C# types.
		val userTypes = file.declarations.filterIsInstance<IrClass>().filter { clrName(it) == null }
		val classes = userTypes.filter { it.kind == ClassKind.CLASS }
		val objects = userTypes.filter { it.kind == ClassKind.OBJECT && !it.isCompanion }
		val interfaces = userTypes.filter { it.kind == ClassKind.INTERFACE }
		val enums = userTypes.filter { it.kind == ClassKind.ENUM_CLASS }
		val functions = file.declarations.filterIsInstance<IrSimpleFunction>()
		// Top-level `val`/`var`/`const val` -> static members of the file class.
		val topProps = file.declarations.filterIsInstance<IrProperty>()
		// Strategy-B opt-in: `@Sm`-marked suspend funcs compile to a state machine on the coroutine runtime.
		val stateMachineFns = functions.filter { it.isSuspend && hasAnnotation(it, CLR_SM) }
		val plainFns = functions.filterNot { it in stateMachineFns }

		if (userTypes.isEmpty() && functions.isEmpty() && topProps.isEmpty()) return ""

		line("// <auto-generated> by kotlin/clr")
		for (e in enums) { generateEnum(e); line("") }
		for (i in interfaces) { generateInterface(i); line("") }
		for (klass in classes) { generateClass(klass); line("") }
		for (obj in objects) { generateObject(obj); line("") }
		for (sm in stateMachineFns) { generateStateMachineClass(sm); line("") }

		if (functions.isNotEmpty() || topProps.isNotEmpty()) {
			line("public static class ${fileClassName(file)}")
			line("{")
			indent++
			for (p in topProps) generateTopLevelProperty(p)
			if (topProps.isNotEmpty()) line("")
			for (function in plainFns) { generateFunction(function, static = true); line("") }
			for (sm in stateMachineFns) { generateStateMachineBridge(sm); line("") }
			functions.firstOrNull { it.name.asString() == "main" && it.parameters.none { p -> p.kind == IrParameterKind.Regular } }
				?.let {
					line("public static void Main(string[] args)")
					line("{")
					indent++
					line("main();")
					indent--
					line("}")
				}
			indent--
			line("}")
		}
		return sb.toString()
	}

	private fun generateInterface(iface: IrClass) {
		line("public interface ${iface.name.asString()}${superList(iface)}")
		line("{")
		indent++
		for (m in iface.memberMethods(requireBody = false)) {
			val params = m.parameters.filter { it.kind == IrParameterKind.Regular }
				.joinToString(", ") { "${csType(it.type)} ${csId(it.name.asString())}" }
			line("${csType(m.returnType)} ${m.name.asString()}($params);")
		}
		indent--
		line("}")
	}

	private fun generateEnum(enum: IrClass) {
		val entries = enum.declarations.filterIsInstance<IrEnumEntry>().joinToString(", ") { it.name.asString() }
		line("public enum ${enum.name.asString()}")
		line("{")
		indent++
		line(entries)
		indent--
		line("}")
	}

	private fun generateClass(klass: IrClass) {
		val abstract = if (klass.modality == Modality.ABSTRACT) "abstract " else ""
		line("public ${abstract}class ${klass.name.asString()}${superList(klass)}")
		line("{")
		indent++
		for (prop in klass.memberProperties()) generateMemberProperty(prop)
		for (ctor in klass.declarations.filterIsInstance<IrConstructor>()) generateConstructor(klass, ctor)
		for (m in klass.memberMethods()) { generateFunction(m, static = false); line("") }
		if (klass.isData) generateDataEquality(klass)
		// A `companion object`'s members become `static` members of the enclosing C# class.
		klass.declarations.filterIsInstance<IrClass>().firstOrNull { it.isCompanion }?.let { emitCompanionMembers(it) }
		indent--
		line("}")
	}

	/** Companion-object members -> `static` fields/methods of the enclosing class (const vals are inlined). */
	private fun emitCompanionMembers(comp: IrClass) {
		for (prop in comp.memberProperties()) {
			val backing = prop.backingField ?: continue
			if (prop.isConst) continue   // const reads are inlined at the use site — no field needed
			val init = (backing.initializer as? IrExpressionBody)?.expression?.let { genExpr(it) }
			val ro = if (!prop.isVar) "readonly " else ""
			line("public static $ro${csType(backing.type)} ${csId(prop.name.asString())} = ${init ?: "default"};")
		}
		for (m in comp.memberMethods()) { generateFunction(m, static = true); line("") }
	}

	/** Value-based Equals/GetHashCode for a data class, generated from its backing fields. */
	private fun generateDataEquality(klass: IrClass) {
		val fields = klass.memberProperties().mapNotNull { it.backingField }.map { csId(it.name.asString()) }
		if (fields.isEmpty()) return
		val name = klass.name.asString()
		line("public override bool Equals(object obj)")
		line("{")
		indent++
		line("return obj is $name o && ${fields.joinToString(" && ") { "global::System.Object.Equals(this.$it, o.$it)" }};")
		indent--
		line("}")
		line("")
		line("public override int GetHashCode()")
		line("{")
		indent++
		line("return global::System.HashCode.Combine(${fields.joinToString(", ") { "this.$it" }});")
		indent--
		line("}")
		line("")
	}

	private fun generateObject(obj: IrClass) {
		// A Kotlin `object` -> sealed C# class with an INSTANCE singleton.
		line("public sealed class ${obj.name.asString()}")
		line("{")
		indent++
		line("public static readonly ${obj.name.asString()} INSTANCE = new ${obj.name.asString()}();")
		for (prop in obj.memberProperties()) generateMemberProperty(prop)
		for (m in obj.memberMethods()) { generateFunction(m, static = false); line("") }
		indent--
		line("}")
	}

	// Real (non-inherited, non-synthetic) members to emit. Fake overrides come from C# inheritance.
	private fun IrClass.memberProperties(): List<IrProperty> =
		declarations.filterIsInstance<IrProperty>().filter { !it.isFakeOverride }

	private fun IrClass.memberMethods(requireBody: Boolean = true): List<IrSimpleFunction> =
		declarations.filterIsInstance<IrSimpleFunction>()
			.filter {
				it.correspondingPropertySymbol == null && !it.isFakeOverride && (!requireBody || it.body != null) &&
					it.name.asString() !in setOf("equals", "hashCode") // value equality bodies deferred
			}

	/** Backing-field property -> C# field; computed property -> C# property with a getter body. */
	private fun generateMemberProperty(prop: IrProperty) {
		val backing = prop.backingField
		if (backing != null) {
			line("public ${csType(backing.type)} ${csId(prop.name.asString())};")
		} else {
			val getter = prop.getter ?: return
			// Mirror the method rule: `override` when overriding a class virtual (e.g. a .NET virtual
			// property like System.Exception.Message); `virtual` when itself open.
			val overridesClass = prop.overriddenSymbols.any { (it.owner.parent as? IrClass)?.kind == ClassKind.CLASS }
			val modifier = when {
				overridesClass -> "override "
				prop.modality == Modality.OPEN || prop.modality == Modality.ABSTRACT -> "virtual "
				else -> ""
			}
			val body = (getter.body as? IrBlockBody)?.statements.orEmpty().joinToString(" ") { renderInline(it) }
			line("public $modifier${csType(getter.returnType)} ${csId(prop.name.asString())} { get { $body } }")
		}
	}

	/** Top-level `const val`/`val`/`var` -> a `const`/`static readonly`/`static` field (or computed property). */
	private fun generateTopLevelProperty(prop: IrProperty) {
		val backing = prop.backingField
		val name = csId(prop.name.asString())
		if (backing != null) {
			val t = csType(backing.type)
			val init = (backing.initializer as? IrExpressionBody)?.expression?.let { genExpr(it) }
			when {
				prop.isConst -> line("public const $t $name = ${init ?: "default"};")
				!prop.isVar -> line("public static readonly $t $name = ${init ?: "default"};")
				else -> line("public static $t $name = ${init ?: "default"};")
			}
		} else {
			val getter = prop.getter ?: return
			val body = (getter.body as? IrBlockBody)?.statements.orEmpty().joinToString(" ") { renderInline(it) }
			line("public static ${csType(getter.returnType)} $name { get { $body } }")
		}
	}

	private fun generateConstructor(klass: IrClass, ctor: IrConstructor) {
		val params = ctor.parameters
			.filter { it.kind == IrParameterKind.Regular }
			.joinToString(", ") { "${csType(it.type)} ${csId(it.name.asString())}" }
		val body = ctor.body as? IrBlockBody
		val delegating = body?.statements?.filterIsInstance<IrDelegatingConstructorCall>()?.firstOrNull()
		val baseClause = delegating?.let { d ->
			val targetFq = (d.symbol.owner.parent as? IrClass)?.fqNameWhenAvailable?.asString()
			val baseArgs = regularArgs(d)
			if (targetFq != "kotlin.Any" && baseArgs.isNotEmpty())
				" : base(${baseArgs.joinToString(", ") { genExpr(it) }})"
			else ""
		} ?: ""
		line("public ${klass.name.asString()}($params)$baseClause")
		line("{")
		indent++
		body?.statements?.forEach { stmt ->
			when (stmt) {
				is IrDelegatingConstructorCall -> {} // handled in the base clause
				is IrInstanceInitializerCall -> emitInstanceInit(klass)
				else -> generateStatement(stmt)
			}
		}
		indent--
		line("}")
	}

	/** Expands field initializers and `init { }` blocks (normally an InitializersLowering job). */
	private fun emitInstanceInit(klass: IrClass) {
		for (decl in klass.declarations) {
			when (decl) {
				is IrProperty -> decl.backingField?.initializer?.let { init ->
					line("this.${decl.name.asString()} = ${genExpr((init as IrExpressionBody).expression)};")
				}
				is IrAnonymousInitializer -> (decl.body as? IrBlockBody)?.statements?.forEach { generateStatement(it) }
				else -> {}
			}
		}
	}

	private fun generateFunction(function: IrSimpleFunction, static: Boolean) {
		// A Kotlin `suspend fun` maps to a C# `async Task<T>` (non-blocking); suspend calls become `await`.
		val ret = when {
			function.isSuspend && function.returnType.isUnit() -> "global::System.Threading.Tasks.Task"
			function.isSuspend -> "global::System.Threading.Tasks.Task<${csType(function.returnType)}>"
			else -> csType(function.returnType)
		}
		val async = if (function.isSuspend) "async " else ""
		// An extension function `fun T.f()` becomes a static method whose first parameter is the receiver
		// (`__self`); references to the receiver inside the body resolve to `__self` (see valSubst below).
		val extRecv = function.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
		val regularParams = function.parameters.filter { it.kind == IrParameterKind.Regular }.map { p ->
			// Constant default arguments become C# optional parameters (`= literal`); the call site can omit them.
			val def = (p.defaultValue?.expression as? IrConst)?.let { " = ${constLiteral(it)}" } ?: ""
			// `vararg xs: T` -> C# `params T[] xs`; the call site spreads its IrVararg into the call args.
			val vararg = if (p.varargElementType != null) "params " else ""
			"$vararg${csType(p.type)} ${csId(p.name.asString())}$def"
		}
		val params = ((extRecv?.let { listOf("${csType(it.type)} __self") } ?: emptyList()) + regularParams).joinToString(", ")
		// `override` only when overriding a class virtual; interface impls and open methods are virtual.
		val overridesClass = function.overriddenSymbols.any {
			(it.owner.parent as? IrClass)?.kind == ClassKind.CLASS
		}
		val modifier = when {
			static -> "static "
			overridesClass -> "override "
			function.modality == Modality.OPEN || function.modality == Modality.ABSTRACT -> "virtual "
			else -> ""
		}
		// `close()` of an AutoCloseable/Closeable implementor -> C# `IDisposable.Dispose()`.
		val ownerImplementsCloseable = (function.parent as? IrClass)?.superTypes
			?.any { it.classFqName?.asString() in setOf("kotlin.AutoCloseable", "java.lang.AutoCloseable", "java.io.Closeable", "kotlin.io.Closeable") } == true
		val methodName = when {
			function.name.asString() == "close" && function.parameters.none { it.kind == IrParameterKind.Regular } && ownerImplementsCloseable -> "Dispose"
			else -> OBJECT_METHODS[function.name.asString()] ?: function.name.asString()
		}
		line("public $modifier$async$ret $methodName($params)")
		line("{")
		indent++
		if (extRecv != null) valSubst[extRecv.name.asString()] = "__self"   // body `this`/receiver -> __self
		when (val body = function.body) {
			is IrBlockBody -> body.statements.forEach { generateStatement(it) }
			else -> line("// unsupported body: ${body?.let { it::class.simpleName }}")
		}
		if (extRecv != null) valSubst.remove(extRecv.name.asString())
		indent--
		line("}")
	}

	// ---- D2.1: compiler-generated coroutine state machine (constrained subset) ----
	// Shape: `suspend fun f(): T { val x0 = e0.await(); ...; val xN = eN.await(); return r }`
	// where each `ei.await()` is the @ClrAwait intrinsic over a .NET Task. Locals become fields;
	// each await is a state boundary; the runtime (IContinuation/Future/TCS) drives suspension.

	// A suspension point: `val x = e.await()` (@ClrAwait over a Task) or `val x = otherSuspend()`
	// (a direct suspend call, whose CLR bridge returns a Task).
	// ----- general CPS lowering of `@Sm suspend fun` to a state machine -----
	//
	// We do not reuse JetBrains' AbstractSuspendFunctionsLowering: its `buildStateMachine` (the actual
	// CPS core) is abstract, so reusing it would still mean porting JsSuspendFunctionsLowering (~46KB)
	// plus building a CLR CommonBackendContext and mapping stdlib coroutine intrinsics. Instead we own
	// the transform end-to-end and target our own runtime directly. The key simplification vs. an
	// IL/IR state machine: we emit C# and can use `goto`, so structured control flow (if/while) with
	// embedded suspension points linearizes to label-dispatch without a full CFG/relooper.
	//
	// Supported: suspension as a `val x = e`/`e` statement or `return e`, inside arbitrary nesting of
	// blocks, `if`/`when` branches, and `while` bodies. Explicitly rejected (loud error, not silent
	// miscompile): suspension nested inside a sub-expression (needs spilling) or in a loop/branch
	// CONDITION. Those are the remaining frontier toward full parity with stdlib lowering.

	private var smState = 0   // next suspension-point state id (incremented as points are emitted)
	private var smLabel = 0   // fresh control-flow label counter

	/** A call that is itself a suspension point: a `@ClrAwait`-bridged `.await()`, or a direct suspend call. */
	private fun isSuspensionCall(e: org.jetbrains.kotlin.ir.IrElement?): Boolean =
		e is IrCall && (hasAnnotation(e.symbol.owner, CLR_AWAIT) || e.symbol.owner.isSuspend)

	/** Does this subtree contain a suspension point anywhere? Decides CPS-linearize vs. emit-as-is. */
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

	/** The awaitable `Task<T>` expression a suspension point starts: the `.await()` receiver, or a bridge call. */
	private fun awaitableExpr(call: IrCall): String =
		if (hasAnnotation(call.symbol.owner, CLR_AWAIT)) extensionReceiverOf(call)?.let { genExpr(it) } ?: "default"
		// A direct suspend call resolves to its sibling Task-returning bridge; the state machine is a
		// separate top-level class, so the bridge (a static on the file class) must be qualified.
		else "global::$fileClass.${genCallInner(call)}"

	private fun freshLabel(): String = "__L${smLabel++}"

	private fun stmtsOf(e: IrExpression): List<org.jetbrains.kotlin.ir.IrStatement> = when (e) {
		is IrBlock -> e.statements
		is IrComposite -> e.statements
		else -> listOf(e)
	}

	/** Variables that must become fields: every variable declared on a suspension-bearing path (so it
	 *  survives across a `return`-and-resume). Variables inside fully-synchronous islands stay locals. */
	private fun collectCpsVars(stmts: List<org.jetbrains.kotlin.ir.IrStatement>, out: MutableList<IrVariable>) {
		for (s in stmts) when (s) {
			is IrVariable -> out.add(s)
			is IrWhen -> if (containsSuspend(s)) s.branches.forEach { collectCpsVars(stmtsOf(it.result), out) }
			is IrWhileLoop -> if (containsSuspend(s)) s.body?.let { collectCpsVars(stmtsOf(it), out) }
			is IrBlock -> if (containsSuspend(s)) collectCpsVars(s.statements, out)
			is IrComposite -> if (containsSuspend(s)) collectCpsVars(s.statements, out)
			else -> {}
		}
	}

	private fun generateStateMachineClass(fn: IrSimpleFunction) {
		smState = 0; smLabel = 0
		val sm = "${fn.name.asString()}__sm"
		val ret = csType(fn.returnType)
		val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val body = (fn.body as? IrBlockBody)?.statements.orEmpty()
		val fieldVars = ArrayList<IrVariable>().also { if (containsSuspend(fn.body ?: return)) collectCpsVars(body, it) }
		val names = params.map { it.name.asString() } + fieldVars.map { it.name.asString() }
		if (names.size != names.toSet().size)
			throw NotImplementedError("@Sm: variable/parameter name shadowing across a suspension point is not yet supported in ${fn.name.asString()}")
		val nSuspends = countSuspends(body)

		line("internal sealed class $sm : global::Kotlin.Coroutines.IContinuation<$ret>")
		line("{")
		indent++
		line("int __label;")
		for (p in params) line("readonly ${csType(p.type)} ${csId(p.name.asString())};")  // params -> fields
		for (v in fieldVars) line("${csType(v.type)} ${csId(v.name.asString())};")          // live locals -> fields
		line("readonly global::Kotlin.Coroutines.IContinuation<$ret> __completion;")
		line("public global::Kotlin.Coroutines.CoroutineContext Context => __completion.Context;")
		val ctorParams = (listOf("global::Kotlin.Coroutines.IContinuation<$ret> c") +
			params.map { "${csType(it.type)} ${csId(it.name.asString())}" }).joinToString(", ")
		val ctorAssign = (listOf("__completion = c;") +
			params.map { "this.${csId(it.name.asString())} = ${csId(it.name.asString())};" }).joinToString(" ")
		line("public $sm($ctorParams) { $ctorAssign }")
		line("public void ResumeWith(global::Kotlin.Coroutines.KResult<object> __r)")
		line("{")
		indent++
		line("try {")
		indent++
		// Dispatch: on (re-)entry, jump to the resume label for the current state. State 0 is the start.
		line("switch (__label) {")
		indent++
		for (s in 0..nSuspends) line("case $s: goto __R$s;")
		indent--
		line("}")
		line("__R0: ;")
		for (s in body) emitCps(s, fn.returnType)
		// Fall off the end (no terminal `return`): complete a Unit coroutine with null.
		if (body.lastOrNull() !is IrReturn)
			line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success((object)null)); return;")
		indent--
		line("} catch (global::System.Exception __e) {")
		indent++
		line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Fail(__e));")
		indent--
		line("}")
		indent--
		line("}")
		indent--
		line("}")
	}

	private fun countSuspends(stmts: List<org.jetbrains.kotlin.ir.IrStatement>): Int {
		var n = 0
		for (s in stmts) s.acceptVoid(object : IrVisitorVoid() {
			override fun visitElement(element: org.jetbrains.kotlin.ir.IrElement) {
				if (isSuspensionCall(element)) n++
				element.acceptChildrenVoid(this)
			}
		})
		return n
	}

	/** Emit one statement in CPS context (inside ResumeWith, goto-land). `ret` = coroutine return type. */
	private fun emitCps(stmt: org.jetbrains.kotlin.ir.IrElement, ret: IrType) {
		when (stmt) {
			is IrVariable -> {
				val name = csId(stmt.name.asString())
				val init = stmt.initializer
				when {
					init != null && isSuspensionCall(init) -> emitSuspend(init as IrCall, name, csType(stmt.type))
					init != null && containsSuspend(init) ->
						throw NotImplementedError("@Sm: suspension nested in an initializer expression is not yet supported (`${stmt.name.asString()}`)")
					init != null -> line("$name = ${genExpr(init)};")
					// no initializer: field already default-initialized
				}
			}
			is IrReturn -> {
				val v = stmt.value
				when {
					isSuspensionCall(v) -> {
						emitSuspend(v as IrCall, null, null)
						if (ret.isUnit()) line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success((object)null)); return;")
						else line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success((object)((${csType(ret)})__r.Value))); return;")
					}
					containsSuspend(v) -> throw NotImplementedError("@Sm: suspension nested in a return expression is not yet supported")
					ret.isUnit() || v.type.isUnit() -> line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success((object)null)); return;")
					else -> line("__completion.ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success((object)(${genExpr(v)}))); return;")
				}
			}
			is IrWhen -> if (containsSuspend(stmt)) emitWhenCps(stmt, ret) else generateStatement(stmt)
			is IrWhileLoop -> if (containsSuspend(stmt)) emitWhileCps(stmt, ret) else generateStatement(stmt)
			is IrBlock -> if (containsSuspend(stmt)) stmt.statements.forEach { emitCps(it, ret) } else generateStatement(stmt)
			is IrComposite -> if (containsSuspend(stmt)) stmt.statements.forEach { emitCps(it, ret) } else generateStatement(stmt)
			is IrCall -> if (isSuspensionCall(stmt)) emitSuspend(stmt, null, null) else generateStatement(stmt)
			else -> {
				if (stmt is IrExpression && containsSuspend(stmt))
					throw NotImplementedError("@Sm: suspension in an unsupported position (${stmt::class.simpleName})")
				generateStatement(stmt)
			}
		}
	}

	/** A suspension point: set the next state, start the awaitable, return; resume label reads the result. */
	private fun emitSuspend(call: IrCall, assignTo: String?, castType: String?) {
		smState++
		val k = smState
		line("__label = $k;")
		line("{ var __t = ${awaitableExpr(call)};")
		line("  __t.GetAwaiter().OnCompleted(() => ResumeWith(__t.IsFaulted")
		line("    ? global::Kotlin.Coroutines.KResult<object>.Fail(__t.Exception.InnerException)")
		line("    : global::Kotlin.Coroutines.KResult<object>.Success((object)__t.Result))); return; }")
		line("__R$k: ;")
		if (assignTo != null) line("$assignTo = ($castType)__r.Value;")
	}

	private fun emitCpsBlock(e: IrExpression, ret: IrType) = stmtsOf(e).forEach { emitCps(it, ret) }

	private fun emitWhenCps(w: IrWhen, ret: IrType) {
		val end = freshLabel()
		for (branch in w.branches) {
			val isElse = branch.condition.let { it is IrConst && it.value == true }
			if (isElse) {
				emitCpsBlock(branch.result, ret)
				line("goto $end;")
			} else {
				if (containsSuspend(branch.condition))
					throw NotImplementedError("@Sm: suspension in a when/if condition is not yet supported")
				val next = freshLabel()
				line("if (!(${genExpr(branch.condition)})) goto $next;")
				emitCpsBlock(branch.result, ret)
				line("goto $end;")
				line("$next: ;")
			}
		}
		line("$end: ;")
	}

	private fun emitWhileCps(loop: IrWhileLoop, ret: IrType) {
		if (containsSuspend(loop.condition))
			throw NotImplementedError("@Sm: suspension in a while condition is not yet supported")
		val start = freshLabel(); val end = freshLabel()
		line("$start: ;")
		line("if (!(${genExpr(loop.condition)})) goto $end;")
		loop.body?.let { emitCpsBlock(it, ret) }
		line("goto $start;")
		line("$end: ;")
	}

	private fun generateStateMachineBridge(fn: IrSimpleFunction) {
		val sm = "${fn.name.asString()}__sm"
		val ret = csType(fn.returnType)
		val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }
		val sig = params.joinToString(", ") { "${csType(it.type)} ${csId(it.name.asString())}" }
		val ctorArgs = (listOf("__root") + params.map { csId(it.name.asString()) }).joinToString(", ")
		line("// ABI: hidden Continuation, exposed as Task<$ret> (driven by the coroutine runtime).")
		line("public static global::System.Threading.Tasks.Task<$ret> ${fn.name.asString()}($sig)")
		line("{")
		indent++
		line("return global::Kotlin.Coroutines.CoroutineBuilders.Future<$ret>(")
		line("    global::Kotlin.Coroutines.CoroutineContext.Empty,")
		line("    __root => new $sm($ctorArgs).ResumeWith(global::Kotlin.Coroutines.KResult<object>.Success(null)));")
		indent--
		line("}")
	}

	/** ` : Base, IFace1, IFace2` — base class first (C# requires it), then interfaces. */
	private fun superList(klass: IrClass): String {
		val supers = klass.superTypes
			.mapNotNull { it.classifierOrNull?.owner as? IrClass }
			.filter { it.fqNameWhenAvailable?.asString() != "kotlin.Any" && (it.kind == ClassKind.CLASS || it.kind == ClassKind.INTERFACE) }
			.sortedBy { if (it.kind == ClassKind.CLASS) 0 else 1 }
		if (supers.isEmpty()) return ""
		return " : " + supers.joinToString(", ") {
			clrName(it)?.let { n -> "global::$n" }
				?: NET_TYPES[it.fqNameWhenAvailable?.asString()]?.let { n -> "global::$n" }   // AutoCloseable -> IDisposable etc.
				?: it.name.asString()
		}
	}

	private fun generateStatement(stmt: org.jetbrains.kotlin.ir.IrElement) {
		when (stmt) {
			is IrVariable -> {
				// `var` lets C# infer the concrete (possibly .NET) type from the initializer.
				val init = stmt.initializer?.let { genExpr(it) }
				// `var` can't infer a nullable value type from a `null`/conditional initializer -> use the explicit type.
				val decl = if (nullableValueType(stmt.type)) csType(stmt.type) else "var"
				if (init != null) line("$decl ${localName(stmt.symbol)} = $init;")
				else line("${csType(stmt.type)} ${localName(stmt.symbol)};")
			}
			is IrSetValue -> line("${localName(stmt.symbol)} = ${genExpr(stmt.value)};")
			is IrSetField -> line("${stmt.receiver?.let { genExpr(it) } ?: "this"}.${csId(stmt.symbol.owner.name.asString())} = ${genExpr(stmt.value)};")
			is IrReturn -> {
				if (stmt.value.type.isUnit()) line("return;")
				else line("return ${genExpr(stmt.value)};")
			}
			is IrWhileLoop -> {
				line("while (${genExpr(stmt.condition)})")
				line("{")
				indent++
				(stmt.body as? IrBlock)?.statements?.forEach { generateStatement(it) }
					?: stmt.body?.let { generateStatement(it) }
				stmt.label?.let { line("${it}__cont: ;") }   // continue@label target
				indent--
				line("}")
				stmt.label?.let { line("${it}__brk: ;") }     // break@label target
			}
			is IrDoWhileLoop -> {
				line("do")
				line("{")
				indent++
				(stmt.body as? IrBlock)?.statements?.forEach { generateStatement(it) }
					?: stmt.body?.let { generateStatement(it) }
				stmt.label?.let { line("${it}__cont: ;") }
				indent--
				line("} while (${genExpr(stmt.condition)});")
				stmt.label?.let { line("${it}__brk: ;") }
			}
			// `break`/`continue`, labeled (`break@outer`) -> C# goto a loop label, plain -> break/continue.
			is IrBreak -> line(stmt.label?.let { "goto ${it}__brk;" } ?: "break;")
			is IrContinue -> line(stmt.label?.let { "goto ${it}__cont;" } ?: "continue;")
			is IrWhen -> generateWhenStatement(stmt)
			is IrTry -> generateTry(stmt)
			is IrBlock -> if (stmt.origin?.toString() == "FOR_LOOP") generateForLoop(stmt)
			else stmt.statements.forEach { generateStatement(it) }
			is IrComposite -> stmt.statements.forEach { generateStatement(it) }
			// A local (nested) function -> a C# local function (no access modifier; C# hoists + closes over locals).
			is IrSimpleFunction -> generateLocalFunction(stmt)
			is IrGetValue -> {}   // a discarded value reference (e.g. the old value from `i++`) is a no-op
			// A value coerced to Unit in statement position (e.g. `i++`) -> emit its inner as statements.
			// A multi-statement block (the `i++` temp) gets its own `{ }` so its temp doesn't collide.
			is IrTypeOperatorCall -> if (stmt.operator == IrTypeOperator.IMPLICIT_COERCION_TO_UNIT) {
				val arg = stmt.argument
				if (arg is IrBlock && arg.statements.size > 1) {
					line("{"); indent++; arg.statements.forEach { generateStatement(it) }; indent--; line("}")
				} else generateStatement(arg)
			} else line("${genExpr(stmt)};")
			is IrExpression -> line("${genExpr(stmt)};")
			else -> line("// unsupported statement: ${stmt::class.simpleName}")
		}
	}

	/** A nested `fun` inside a body -> a C# local function (no modifiers; captures enclosing locals). */
	private fun generateLocalFunction(fn: IrSimpleFunction) {
		val params = fn.parameters.filter { it.kind == IrParameterKind.Regular }.joinToString(", ") { p ->
			val def = (p.defaultValue?.expression as? IrConst)?.let { " = ${constLiteral(it)}" } ?: ""
			val vararg = if (p.varargElementType != null) "params " else ""
			"$vararg${csType(p.type)} ${csId(p.name.asString())}$def"
		}
		line("${csType(fn.returnType)} ${csId(fn.name.asString())}($params)")
		line("{")
		indent++
		(fn.body as? IrBlockBody)?.statements?.forEach { generateStatement(it) }
		indent--
		line("}")
	}

	private fun generateWhenStatement(whenExpr: IrWhen) {
		whenExpr.branches.forEachIndexed { index, branch ->
			val isElse = branch.condition.let { it is IrConst && it.value == true }
			when {
				index == 0 -> line("if (${genExpr(branch.condition)})")
				isElse -> line("else")
				else -> line("else if (${genExpr(branch.condition)})")
			}
			line("{")
			indent++
			generateStatement(branch.result)
			indent--
			line("}")
		}
	}

	private fun generateTry(tryExpr: IrTry) {
		line("try")
		line("{")
		indent++
		emitBlockOrStatement(tryExpr.tryResult)
		indent--
		line("}")
		for (catch in tryExpr.catches) {
			val p = catch.catchParameter
			line("catch (${csType(p.type)} ${p.name.asString()})")
			line("{")
			indent++
			emitBlockOrStatement(catch.result)
			indent--
			line("}")
		}
		tryExpr.finallyExpression?.let {
			line("finally")
			line("{")
			indent++
			emitBlockOrStatement(it)
			indent--
			line("}")
		}
	}

	private fun emitBlockOrStatement(expr: IrExpression) {
		if (expr is IrBlock) expr.statements.forEach { generateStatement(it) } else generateStatement(expr)
	}

	/** A Kotlin `for (x in ...)` is desugared to iterator+while; re-fold it into a C# for/foreach. */
	private fun generateForLoop(block: IrBlock) {
		val iterVar = block.statements.getOrNull(0) as? IrVariable
		val whileLoop = block.statements.getOrNull(1) as? IrWhileLoop
		val bodyBlock = whileLoop?.body as? IrBlock
		val loopVar = bodyBlock?.statements?.getOrNull(0) as? IrVariable
		if (iterVar == null || bodyBlock == null || loopVar == null) {
			block.statements.forEach { generateStatement(it) }
			return
		}
		val loopName = loopVar.name.asString()
		val realBody = bodyBlock.statements.drop(1)
		// The iterable is the receiver of `.iterator()`.
		val source = (iterVar.initializer as? IrCall)?.let { dispatchReceiverOf(it) }
		val header = (source as? IrCall)?.let { rangeForHeader(loopName, it) }
			?: "foreach (var $loopName in ${source?.let { genExpr(it) } ?: "default"})"
		line(header)
		line("{")
		indent++
		realBody.forEach { generateStatement(it) }
		whileLoop.label?.let { line("${it}__cont: ;") }   // continue@label -> falls to the increment
		indent--
		line("}")
		whileLoop.label?.let { line("${it}__brk: ;") }     // break@label target
	}

	/** A C# `for` header for a Kotlin range (`a..b`, `a until b`, `a downTo b`), or null. */
	private fun rangeForHeader(name: String, range: IrCall): String? {
		val ops = operandList(range)
		if (ops.size != 2) return null
		val a = genExpr(ops[0]); val b = genExpr(ops[1])
		return when (range.symbol.owner.name.asString()) {
			"rangeTo" -> "for (int $name = $a; $name <= $b; $name++)"
			"until", "rangeUntil" -> "for (int $name = $a; $name < $b; $name++)"
			"downTo" -> "for (int $name = $a; $name >= $b; $name--)"
			else -> null
		}
	}

	// ----- expressions -----

	private fun genExpr(expr: IrExpression): String = when (expr) {
		is IrConst -> constLiteral(expr)
		is IrGetValue -> valSubst[expr.symbol.owner.name.asString()] ?: localName(expr.symbol)
		is IrThrow -> "throw ${genExpr(expr.value)}"
		is IrBlock -> genBlockExpr(expr)
		is IrGetField -> "${expr.receiver?.let { genExpr(it) } ?: "this"}.${csId(expr.symbol.owner.name.asString())}"
		is IrStringConcatenation -> "string.Concat(${expr.arguments.joinToString(", ") { genExpr(it) }})"
		is IrWhen -> ternary(expr)
		is IrConstructorCall -> genConstructorCall(expr)
		is IrCall -> genCall(expr)
		is IrFunctionExpression -> renderLambda(expr)
		is IrGetObjectValue -> clrName(expr.symbol.owner)?.let { "global::$it" }
			?: expr.symbol.owner.let { o -> if (o.isCompanion) (o.parent as IrClass).name.asString() else "${o.name.asString()}.INSTANCE" }
		is IrGetEnumValue -> "${csType(expr.type)}.${expr.symbol.owner.name.asString()}"
		is IrTypeOperatorCall -> genTypeOperator(expr)
		// A `vararg` argument: spread its elements into the comma-separated call-arg list (`params` target).
		is IrVararg -> expr.elements.filterIsInstance<IrExpression>().joinToString(", ") { genExpr(it) }
		else -> "/* unsupported expr: ${expr::class.simpleName} */ default"
	}

	private fun genTypeOperator(expr: IrTypeOperatorCall): String = when (expr.operator) {
		IrTypeOperator.CAST, IrTypeOperator.SAFE_CAST ->
			"((${csType(expr.typeOperand)})${genExpr(expr.argument)})"
		// `x is T` / `x !is T` (used by `when (x) { is T -> }` exhaustive matching).
		IrTypeOperator.INSTANCEOF -> "(${genExpr(expr.argument)} is ${csType(expr.typeOperand)})"
		IrTypeOperator.NOT_INSTANCEOF -> "!(${genExpr(expr.argument)} is ${csType(expr.typeOperand)})"
		// Smart cast (`when (x) { is T -> x.member }`) — emit the C# downcast so member access compiles.
		IrTypeOperator.IMPLICIT_CAST -> "((${csType(expr.typeOperand)})${genExpr(expr.argument)})"
		// Implicit casts / coercions to Unit / notnull just pass the value through.
		else -> genExpr(expr.argument)
	}

	// Primitive string-conversion is CLR-native (a Kotlin.NET program is a .NET program): booleans print
	// `True`/`False`, doubles `4`, matching the host platform for interop consistency. The differential
	// harness normalizes these platform-cosmetic differences vs kotlin/jvm.

	/** A Kotlin lambda becomes a C# lambda, which the CLR binds to the target delegate (e.g. Action). */
	private fun renderLambda(expr: IrFunctionExpression): String {
		val fn = expr.function
		val params = fn.parameters
			.filter { it.kind == IrParameterKind.Regular }
			.joinToString(", ") { csId(it.name.asString()) }
		val statements = (fn.body as? IrBlockBody)?.statements.orEmpty()
		// A non-Unit lambda's last expression is its implicit return value — emit `return` so the C#
		// lambda is a value-returning Func (needed for LINQ transforms, Func delegates, event handlers).
		val body = if (!fn.returnType.isUnit() && statements.isNotEmpty() && statements.last() is IrExpression && statements.last() !is IrReturn) {
			val init = statements.dropLast(1).joinToString(" ") { renderInline(it) }
			"$init return ${genExpr(statements.last() as IrExpression)};".trim()
		} else statements.joinToString(" ") { renderInline(it) }
		val async = if (fn.isSuspend) "async " else ""
		return "$async($params) => { $body }"
	}

	/** Single-line rendering of a statement, for use inside lambda bodies. */
	private fun renderInline(stmt: org.jetbrains.kotlin.ir.IrElement): String = when (stmt) {
		is IrVariable -> stmt.initializer?.let { "${if (nullableValueType(stmt.type)) csType(stmt.type) else "var"} ${localName(stmt.symbol)} = ${genExpr(it)};" } ?: "${csType(stmt.type)} ${localName(stmt.symbol)};"
		is IrSetValue -> "${localName(stmt.symbol)} = ${genExpr(stmt.value)};"
		is IrSetField -> "${stmt.receiver?.let { genExpr(it) } ?: "this"}.${csId(stmt.symbol.owner.name.asString())} = ${genExpr(stmt.value)};"
		is IrReturn -> if (stmt.value.type.isUnit()) "return;" else "return ${genExpr(stmt.value)};"
		is IrExpression -> "${genExpr(stmt)};"
		else -> "/* unsupported inline stmt: ${stmt::class.simpleName} */"
	}

	/** A block in expression position: a `when (subject)` lowers to `{ val tmp = subject; WHEN }`. */
	private fun genBlockExpr(block: IrBlock): String {
		val tmp = block.statements.getOrNull(0) as? IrVariable
		val whenExpr = block.statements.getOrNull(1) as? IrWhen
		if (block.statements.size == 2 && tmp != null && whenExpr != null && tmp.initializer != null) {
			val key = tmp.name.asString()
			// Elvis `a ?: b` -> C# `(a ?? b)` (collapses the nullable; b is the null-branch result).
			if (block.origin?.toString() == "ELVIS") {
				return "(${genExpr(tmp.initializer!!)} ?? ${genExpr(whenExpr.branches.first().result)})"
			}
			valSubst[key] = "(${genExpr(tmp.initializer!!)})"
			// Safe call `a?.b` -> `(a == null ? (T?)null : a.b)`, casting null to the (nullable) result type.
			if (block.origin?.toString() == "SAFE_CALL") {
				val recv = valSubst[key]!!
				val elseResult = genExpr(whenExpr.branches.last().result)
				valSubst.remove(key)
				// csType already adds `?` for nullable value types — don't double it.
				val t = csType(block.type).let { if (it.endsWith("?")) it else "$it?" }
				return "($recv == null ? ($t)null : $elseResult)"
			}
			val result = ternary(whenExpr)
			valSubst.remove(key)
			return result
		}
		// Generic block-expression: value is the last statement.
		return (block.statements.lastOrNull() as? IrExpression)?.let { genExpr(it) } ?: "default"
	}

	private fun ternary(whenExpr: IrWhen): String {
		// Fold branches right-to-left into nested conditional expressions.
		var result = "default"
		for (branch in whenExpr.branches.asReversed()) {
			val isElse = branch.condition.let { it is IrConst && it.value == true }
			result = if (isElse) genExpr(branch.result)
			else "(${genExpr(branch.condition)} ? ${genExpr(branch.result)} : $result)"
		}
		return result
	}

	private fun genCall(call: IrCall): String {
		// A call to a `suspend fun` returns a Task in C#; await it (non-blocking).
		// `await` binds tightly, and as a statement `await X;` is valid (unlike `(await X);`).
		if (call.symbol.owner.isSuspend) return "await ${genCallInner(call)}"
		return genCallInner(call)
	}

	private fun genCallInner(call: IrCall): String {
		val callee = call.symbol.owner
		// `@ClrAwait` intrinsic (`suspend fun <T> Task<T>.await(): T`): the awaitable IS the receiver.
		// The suspend wrapper in genCall turns this into `await <receiver>` — the generic interop point.
		if (hasAnnotation(callee, CLR_AWAIT)) {
			return extensionReceiverOf(call)?.let { genExpr(it) } ?: "default"
		}
		val name = callee.name.asString()
		val declaringClass = callee.parent as? IrClass
		val clrType = declaringClass?.let { clrName(it) }
		val declFq = declaringClass?.fqNameWhenAvailable?.asString()
			?: (callee.parent as? IrPackageFragment)?.packageFqName?.asString()
		val isBuiltin = declFq == null || declFq.startsWith("kotlin")

		// `val (a, b) = pair` destructuring: Pair/Triple.componentN() -> ValueTuple `.ItemN`.
		if (name.startsWith("component") && name.drop("component".length).all { it.isDigit() } &&
			(declFq == "kotlin.Pair" || declFq == "kotlin.Triple")) {
			return "${dispatchReceiverOf(call)?.let { genExpr(it) }}.Item${name.removePrefix("component")}"
		}

		// Enum static API: `EnumType.values()`/`entries` -> `ToList(Enum.GetValues<T>())` (List<T>: size/foreach/index),
		// `EnumType.valueOf(s)` -> `Enum.Parse<T>(s)`.
		if (declaringClass?.kind == ClassKind.ENUM_CLASS) {
			val et = declaringClass.name.asString()
			when (name) {
				"values" -> return "global::System.Linq.Enumerable.ToList(global::System.Enum.GetValues<$et>())"
				"valueOf" -> return "global::System.Enum.Parse<$et>(${genExpr(regularArgs(call).first())})"
			}
		}

		// Numeric conversion `x.toLong()`/`x.toInt()`/… (receiver is a number) -> a C# cast.
		if (isBuiltin && name in NUMBER_CONV) {
			val recv = dispatchReceiverOf(call) ?: extensionReceiverOf(call)
			val rfq = recv?.type?.classFqName?.asString()
			if (recv != null && rfq in PRIMITIVES && rfq != "kotlin.String" && rfq != "kotlin.Boolean")
				return "(${NUMBER_CONV[name]})${genExpr(recv)}"
		}

		// Array / collection indexing `a[i]` / `a[i] = v` / `map[k]` -> C# `[]` access.
		if (callee.isOperator && (name == "get" || name == "set") &&
			dispatchReceiverOf(call)?.type?.let { isArrayType(it) || isIndexableCollection(it) } == true) {
			val target = genExpr(dispatchReceiverOf(call)!!)
			val a = regularArgs(call)
			return if (name == "get") "$target[${a.joinToString(", ") { genExpr(it) }}]"
			else "$target[${a.dropLast(1).joinToString(", ") { genExpr(it) }}] = ${genExpr(a.last())}"
		}

		// Indexer: `list[i]` / `list[i] = v` on a @Clr type -> C# indexer syntax.
		if (clrType != null && callee.isOperator && (name == "get" || name == "set")) {
			val target = memberTarget(call, clrType)
			val args = regularArgs(call)
			return if (name == "get") "$target[${args.joinToString(", ") { genExpr(it) }}]"
			else "$target[${args.dropLast(1).joinToString(", ") { genExpr(it) }}] = ${genExpr(args.last())}"
		}

		// Array factories (`arrayOf`/`intArrayOf`/…) -> a C# array literal `new T[] { ... }`.
		val calleeFq = callee.fqNameOrNull()
		if (calleeFq in ARRAY_FACTORIES) {
			val v = call.arguments.firstOrNull() as? IrVararg
			val elems = v?.elements.orEmpty().filterIsInstance<IrExpression>().joinToString(", ") { genExpr(it) }
			val elem = v?.let { csType(it.varargElementType) } ?: "object"
			return "new $elem[] { $elems }"
		}

		// Kotlin collection ops -> LINQ (static form, no `using` needed). Lambdas value-return (renderLambda).
		if (calleeFq in COLLECTION_OPS) {
			val LE = "global::System.Linq.Enumerable"
			val recv = extensionReceiverOf(call)?.let { genExpr(it) } ?: "default"
			val a = regularArgs(call).map { genExpr(it) }                 // lambda(s)/value args
			val a0 = a.getOrNull(0); val a1 = a.getOrNull(1)
			return when (calleeFq!!.removePrefix("kotlin.collections.")) {
				// element-producing -> materialize to List (Kotlin returns List, not a lazy sequence).
				"map" -> "$LE.ToList($LE.Select($recv, $a0))"
				"filter" -> "$LE.ToList($LE.Where($recv, $a0))"
				"flatMap" -> "$LE.ToList($LE.SelectMany($recv, $a0))"
				"take" -> "$LE.ToList($LE.Take($recv, $a0))"
				"drop" -> "$LE.ToList($LE.Skip($recv, $a0))"
				"sortedBy" -> "$LE.ToList($LE.OrderBy($recv, $a0))"
				"reversed" -> "$LE.ToList($LE.Reverse($recv))"
				"forEach" -> "$LE.ToList($recv).ForEach($a0)"
				// scalar-producing.
				"fold" -> "$LE.Aggregate($recv, $a0, $a1)"                 // fold(initial, (acc,e)->…)
				"any" -> if (a0 != null) "$LE.Any($recv, $a0)" else "$LE.Any($recv)"
				"all" -> "$LE.All($recv, $a0)"
				"count" -> if (a0 != null) "$LE.Count($recv, $a0)" else "$LE.Count($recv)"
				"sum" -> "$LE.Sum($recv)"
				"first" -> if (a0 != null) "$LE.First($recv, $a0)" else "$LE.First($recv)"
				"find" -> "$LE.FirstOrDefault($recv, $a0)"
				"firstOrNull" -> if (a0 != null) "$LE.FirstOrDefault($recv, $a0)" else "$LE.FirstOrDefault($recv)"
				"lastOrNull" -> if (a0 != null) "$LE.LastOrDefault($recv, $a0)" else "$LE.LastOrDefault($recv)"
				"last" -> if (a0 != null) "$LE.Last($recv, $a0)" else "$LE.Last($recv)"
				"none" -> if (a0 != null) "!$LE.Any($recv, $a0)" else "!$LE.Any($recv)"
				"single" -> if (a0 != null) "$LE.Single($recv, $a0)" else "$LE.Single($recv)"
				"sumOf" -> "$LE.Sum($LE.Select($recv, $a0))"
				"maxByOrNull" -> "$LE.MaxBy($recv, $a0)"
				"minByOrNull" -> "$LE.MinBy($recv, $a0)"
				// dictionary-producing.
				"groupBy" -> "$LE.ToDictionary($LE.GroupBy($recv, $a0), (__g) => __g.Key, (__g) => $LE.ToList(__g))"
				"associateBy" -> "$LE.ToDictionary($recv, $a0)"
				"associateWith" -> "$LE.ToDictionary($recv, (__x) => __x, $a0)"
				"zip" -> "$LE.ToList($LE.Zip($recv, $a0, ${a1 ?: "(__x, __y) => global::System.ValueTuple.Create(__x, __y)"}))"
				"reduce" -> "$LE.Aggregate($recv, $a0)"
				"distinct" -> "$LE.ToList($LE.Distinct($recv))"
				"sorted" -> "$LE.ToList($LE.OrderBy($recv, (__x) => __x))"
				"toSet" -> "$LE.ToHashSet($recv)"
				"toList" -> "$LE.ToList($recv)"
				"maxOrNull" -> "$LE.Max($recv)"
				"minOrNull" -> "$LE.Min($recv)"
				"average" -> "$LE.Average($recv)"
				"contains" -> "$LE.Contains($recv, $a0)"
				"isEmpty" -> "($recv.Count == 0)"
				"isNotEmpty" -> "($recv.Count != 0)"
				"joinToString" -> "string.Join(${a0 ?: "\", \""}, $recv)"
				else -> recv
			}
		}

		// kotlin.math.* -> System.Math.* (top-level functions, no receiver).
		MATH_FUNCS[calleeFq]?.let { m ->
			return "global::System.Math.$m(${regularArgs(call).joinToString(", ") { genExpr(it) }})"
		}
		// `s.split(",")` -> String.Split(delims, None) materialized to a List<String>.
		if (calleeFq == "kotlin.text.split") {
			val recv = extensionReceiverOf(call)?.let { genExpr(it) } ?: "default"
			val delims = (regularArgs(call).firstOrNull() as? IrVararg)?.elements.orEmpty()
				.filterIsInstance<IrExpression>().joinToString(", ") { genExpr(it) }
			return "global::System.Linq.Enumerable.ToList($recv.Split(new string[] { $delims }, global::System.StringSplitOptions.None))"
		}
		// kotlin.text String operations -> .NET String methods on the receiver.
		STRING_OPS[calleeFq]?.let { m ->
			val recv = (extensionReceiverOf(call) ?: dispatchReceiverOf(call))?.let { genExpr(it) } ?: "default"
			return "$recv.$m(${regularArgs(call).joinToString(", ") { genExpr(it) }})"
		}
		// String -> number (`"42".toInt()` -> Int32.Parse), Char predicates, and coerce* -> Math.*.
		run {
			val extRecv = extensionReceiverOf(call)?.let { genExpr(it) } ?: "default"
			val a0 = regularArgs(call).getOrNull(0)?.let { genExpr(it) }
			val a1 = regularArgs(call).getOrNull(1)?.let { genExpr(it) }
			NUMBER_PARSE[calleeFq]?.let { p -> return "$p.Parse($extRecv)" }
			CHAR_OPS[calleeFq]?.let { m -> return "global::System.Char.$m($extRecv)" }
			val LE = "global::System.Linq.Enumerable"
			when (calleeFq) {
				"kotlin.ranges.coerceAtMost" -> return "global::System.Math.Min($extRecv, $a0)"
				"kotlin.ranges.coerceAtLeast" -> return "global::System.Math.Max($extRecv, $a0)"
				"kotlin.ranges.coerceIn" -> return "global::System.Math.Clamp($extRecv, $a0, $a1)"
				"kotlin.text.repeat" -> return "string.Concat($LE.Repeat($extRecv, $a0))"
				"kotlin.text.reversed" -> return "new string($LE.ToArray($LE.Reverse($extRecv)))"
			}
		}

		// `repeat(n) { i -> … }` -> iterate 0..<n (index bound to the lambda param).
		if (calleeFq == "kotlin.repeat") {
			val n = regularArgs(call).getOrNull(0)?.let { genExpr(it) } ?: "0"
			val lambda = regularArgs(call).getOrNull(1)?.let { genExpr(it) } ?: "(i) => {}"
			return "global::System.Linq.Enumerable.ToList(global::System.Linq.Enumerable.Range(0, $n)).ForEach($lambda)"
		}
		// `resource.use { it -> … }` -> a C# IIFE with try/finally Dispose (Closeable -> IDisposable).
		if (calleeFq == "kotlin.io.use" || calleeFq == "kotlin.use") {
			val recvExpr = extensionReceiverOf(call)
			val lambda = regularArgs(call).firstOrNull() as? IrFunctionExpression
			if (recvExpr != null && lambda != null) {
				val fn = lambda.function
				val pname = fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }?.let { csId(it.name.asString()) } ?: "it"
				val tx = csType(recvExpr.type)
				val unit = call.type.isUnit()
				val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
				val inner = if (unit) stmts.joinToString(" ") { renderInline(it) } else {
					val init = stmts.dropLast(1).joinToString(" ") { renderInline(it) }
					val last = stmts.lastOrNull()
					("$init " + if (last is IrExpression && last !is IrReturn) "return ${genExpr(last)};" else last?.let { renderInline(it) } ?: "").trim()
				}
				val fin = "finally { ($pname as global::System.IDisposable)?.Dispose(); }"
				val del = if (unit) "global::System.Action<$tx>" else "global::System.Func<$tx, ${csType(call.type)}>"
				return "(($del)(($pname) => { try { $inner } $fin }))(${genExpr(recvExpr)})"
			}
		}

		// Scope functions (inline stdlib) -> a C# IIFE. apply/also return the receiver; let/run/with
		// return the lambda result. The receiver (`this$...`) or `it` binds to the IIFE parameter.
		if (calleeFq in SCOPE_FUNCTIONS) {
			val isWith = calleeFq == "kotlin.with"
			val recvExpr = if (isWith) regularArgs(call).getOrNull(0) else extensionReceiverOf(call)
			val lambda = (if (isWith) regularArgs(call).getOrNull(1) else regularArgs(call).getOrNull(0)) as? IrFunctionExpression
			if (recvExpr != null && lambda != null) {
				val fn = lambda.function
				val recvParam = fn.parameters.firstOrNull { it.kind == IrParameterKind.ExtensionReceiver }
				val pname = if (recvParam != null) "__scope"
					else fn.parameters.firstOrNull { it.kind == IrParameterKind.Regular }?.let { csId(it.name.asString()) } ?: "__scope"
				if (recvParam != null) valSubst[recvParam.name.asString()] = pname
				val stmts = (fn.body as? IrBlockBody)?.statements.orEmpty()
				val returnsRecv = calleeFq == "kotlin.apply" || calleeFq == "kotlin.also"
				val tx = csType(recvExpr.type)
				val unit = call.type.isUnit()
				val retType = when { returnsRecv -> tx; unit -> "void"; else -> csType(call.type) }
				val body = when {
					returnsRecv -> (stmts.joinToString(" ") { renderInline(it) } + " return $pname;").trim()
					unit -> stmts.joinToString(" ") { renderInline(it) }
					else -> {
						val init = stmts.dropLast(1).joinToString(" ") { renderInline(it) }
						val last = stmts.lastOrNull()
						("$init " + if (last is IrExpression && last !is IrReturn) "return ${genExpr(last)};" else last?.let { renderInline(it) } ?: "").trim()
					}
				}
				if (recvParam != null) valSubst.remove(recvParam.name.asString())
				val del = if (retType == "void") "global::System.Action<$tx>" else "global::System.Func<$tx, $retType>"
				return "(($del)(($pname) => { $body }))(${genExpr(recvExpr)})"
			}
		}

		// `mapOf(k to v, ...)` -> a C# Dictionary with indexer initializers. Each element is a `to` call.
		if (calleeFq in MAP_FACTORIES) {
			val pairs = (call.arguments.firstOrNull() as? IrVararg)?.elements.orEmpty().filterIsInstance<IrCall>()
			val entries = pairs.joinToString(", ") { p ->
				"[${extensionReceiverOf(p)?.let { genExpr(it) }}] = ${genExpr(regularArgs(p).first())}"
			}
			return "new global::System.Collections.Generic.Dictionary${csTypeArgs(call.type)} { $entries }"
		}

		// Kotlin collection factories -> a C# generic-collection literal.
		if (calleeFq in LIST_FACTORIES || calleeFq in SET_FACTORIES) {
			val elems = (call.arguments.firstOrNull() as? IrVararg)?.elements.orEmpty()
				.filterIsInstance<IrExpression>().joinToString(", ") { genExpr(it) }
			val coll = if (calleeFq in SET_FACTORIES) "HashSet" else "List"
			return "new global::System.Collections.Generic.$coll${csTypeArgs(call.type)} { $elems }"
		}

		// Property get/set (both @Clr and user classes) -> C# property access.
		val property = callee.correspondingPropertySymbol?.owner
		if (property != null) {
			// Enum intrinsics: `e.name` -> `e.ToString()`, `e.ordinal` -> `(int)e`, `E.entries` -> values list.
			val recvExpr = dispatchReceiverOf(call) ?: extensionReceiverOf(call)
			val recvIsEnum = (recvExpr?.type?.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS
			if (recvIsEnum) {
				val recv = recvExpr?.let { genExpr(it) } ?: "default"
				when (property.name.asString()) {
					"name" -> return "$recv.ToString()"
					"ordinal" -> return "(int)$recv"
				}
			}
			if (property.name.asString() == "entries" && declaringClass?.kind == ClassKind.ENUM_CLASS)
				return "global::System.Linq.Enumerable.ToList(global::System.Enum.GetValues<${declaringClass.name.asString()}>())"
			// Array `.size` is `.Length` in C# (not `.Count`).
			if (property.name.asString() == "size" && dispatchReceiverOf(call)?.type?.let(::isArrayType) == true)
				return "${genExpr(dispatchReceiverOf(call)!!)}.Length"
			// `Char.code` (an extension property) -> the integer code point `(int)c`.
			if (property.name.asString() == "code" && declFq?.startsWith("kotlin") == true)
				return "(int)${(dispatchReceiverOf(call) ?: extensionReceiverOf(call))?.let { genExpr(it) }}"
			// Top-level property (parent is the file/package, not a class) -> a bare sibling static field.
			if (declaringClass == null) {
				val n = csId(property.name.asString())
				return if (callee === property.setter) "$n = ${genExpr(regularArgs(call).first())}" else n
			}
			val propName = BUILTIN_PROPS[property.name.asString()]?.takeIf { declFq?.startsWith("kotlin") == true }
				?: clrName(property) ?: property.name.asString()
			val target = memberTarget(call, clrType ?: "")
			return if (callee === property.setter) "$target.$propName = ${genExpr(regularArgs(call).first())}"
			else "$target.$propName"
		}

		// Range membership: `x in a..b` -> `(x >= a && x <op> b)`.
		if (isBuiltin && name == "contains") {
			val range = dispatchReceiverOf(call) as? IrCall
			val value = regularArgs(call).firstOrNull()
			if (range != null && value != null) rangeForHeader("_", range)?.let {
				val ops = operandList(range)
				val op = if (range.symbol.owner.name.asString() == "rangeTo") "<=" else "<"
				return "(${genExpr(value)} >= ${genExpr(ops[0])} && ${genExpr(value)} $op ${genExpr(ops[1])})"
			}
		}

		// `e!!` (not-null assertion) -> the value itself (C#'s use site throws on null anyway).
		if (isBuiltin && name == "CHECK_NOT_NULL") return genExpr(regularArgs(call).first())

		// Built-in operators on primitives/intrinsics (NOT user methods that happen to be named `plus`).
		if (isBuiltin) {
			val operands = operandList(call)
			// Structural equality: `==` for value types/strings, else System.Object.Equals (calls .Equals).
			if ((name == "EQEQ" || name == "EQEQEQ") && operands.size == 2) {
				return if (operands.all { isEqByValue(it.type) })
					"(${genExpr(operands[0])} == ${genExpr(operands[1])})"
				else "global::System.Object.Equals(${genExpr(operands[0])}, ${genExpr(operands[1])})"
			}
			BINARY_OPERATORS[name]?.let { op ->
				if (operands.size == 2) return "(${genExpr(operands[0])} $op ${genExpr(operands[1])})"
			}
			UNARY_OPERATORS[name]?.let { op ->
				if (operands.size == 1) return "($op${genExpr(operands[0])})"
			}
			// `i.inc()`/`i.dec()` (the `i++`/`i--` desugaring) -> `(i + 1)`/`(i - 1)`.
			if (name == "inc" && operands.size == 1) return "(${genExpr(operands[0])} + 1)"
			if (name == "dec" && operands.size == 1) return "(${genExpr(operands[0])} - 1)"
			val fqName = callee.fqNameOrNull()
			if (fqName == "kotlin.io.println") return "System.Console.WriteLine(${regularArgs(call).joinToString(", ") { genExpr(it) }})"
			if (fqName == "kotlin.io.print") return "System.Console.Write(${regularArgs(call).joinToString(", ") { genExpr(it) }})"
			// Synthetic else of an exhaustive `when` -> a C# throw expression (valid in ternary position).
			if (name == "noWhenBranchMatchedException" || name == "throwUninitializedPropertyAccessException")
				return "throw new global::System.InvalidOperationException(\"$name\")"
			// Precondition / error helpers.
			if (fqName == "kotlin.TODO") return "throw new global::System.NotImplementedException()"
			if (fqName == "kotlin.error")
				return "throw new global::System.InvalidOperationException(${regularArgs(call).firstOrNull()?.let { genExpr(it) } ?: "\"error\""})"
			// Collection `isEmpty()`/`isNotEmpty()` (member, not extension) -> `.Count == 0`.
			if (name == "isEmpty" || name == "isNotEmpty") {
				val recv = dispatchReceiverOf(call) ?: extensionReceiverOf(call)
				val r = recv?.let { genExpr(it) } ?: "default"
				val len = if (recv?.type?.classFqName?.asString() == "kotlin.String") "$r.Length" else "$r.Count"  // string: Length
				return if (name == "isEmpty") "($len == 0)" else "($len != 0)"
			}
			// String blank checks -> System.String.IsNullOrWhiteSpace.
			if (name == "isBlank" || name == "isNotBlank") {
				val r = (extensionReceiverOf(call) ?: dispatchReceiverOf(call))?.let { genExpr(it) } ?: "default"
				return if (name == "isBlank") "string.IsNullOrWhiteSpace($r)" else "!string.IsNullOrWhiteSpace($r)"
			}
			if (fqName == "kotlin.require")
				return "if (!(${genExpr(regularArgs(call).first())})) throw new global::System.ArgumentException(\"Failed requirement\")"
			if (fqName == "kotlin.check")
				return "if (!(${genExpr(regularArgs(call).first())})) throw new global::System.InvalidOperationException(\"Check failed\")"
			// `a to b` -> a C# value tuple `(a, b)` (Pair/Triple map to ValueTuple; see csType).
			if (fqName == "kotlin.to")
				return "(${extensionReceiverOf(call)?.let { genExpr(it) }}, ${genExpr(regularArgs(call).first())})"
		}

		// Method on a @Clr type -> static (object/companion) or instance (receiver.Member) call.
		if (clrType != null) {
			// I4: an injected `add_<E>`/`remove_<E>` call is a .NET event subscription -> `recv.<E> += handler`.
			clrc.ClrEventRegistry.lookup(declFq, name)?.let { (eventName, op) ->
				return "${memberTarget(call, clrType)}.$eventName $op ${genExpr(regularArgs(call).first())}"
			}
			val member = clrName(callee) ?: OBJECT_METHODS[name] ?: name   // toString/equals/hashCode -> .NET names
			val args = regularArgs(call).joinToString(", ") { genExpr(it) }
			return "${memberTarget(call, clrType)}.$member($args)"
		}

		// User extension function `fun T.f(...)` -> static `f(receiver, args...)` (receiver is __self param).
		val extRecv = extensionReceiverOf(call)
		if (extRecv != null && !isBuiltin) {
			val all = (listOf(genExpr(extRecv)) + regularArgs(call).map { genExpr(it) }).joinToString(", ")
			return "${OBJECT_METHODS[name] ?: name}($all)"
		}
		// User-declared call: instance method (`recv.m(...)`) or sibling top-level (`m(...)`).
		val receiver = dispatchReceiverOf(call)
		val args = regularArgs(call).joinToString(", ") { genExpr(it) }
		val csMethod = OBJECT_METHODS[name] ?: name
		// Companion-object member call -> a `static` call on the enclosing class (`Circle.unit()`).
		val recvObj = (receiver as? IrGetObjectValue)?.symbol?.owner
		if (recvObj?.isCompanion == true) return "${(recvObj.parent as IrClass).name.asString()}.$csMethod($args)"
		return if (receiver != null && receiver !is IrGetObjectValue) "${genExpr(receiver)}.$csMethod($args)"
		else "$csMethod($args)"
	}

	private fun genConstructorCall(call: IrConstructorCall): String {
		val klass = call.symbol.owner.parent as? IrClass
		val clrType = klass?.let { clrName(it) }
		val args = regularArgs(call).joinToString(", ") { genExpr(it) }
		val typeName = clrType?.let { "global::$it${csTypeArgs(call.type)}" } ?: klass?.name?.asString() ?: "object"
		return "new $typeName($args)"
	}

	/** Renders C# generic type arguments, e.g. `<int, string>`, from a parameterized type. */
	private fun csTypeArgs(type: IrType): String {
		val args = (type as? IrSimpleType)?.arguments.orEmpty()
		if (args.isEmpty()) return ""
		return "<" + args.joinToString(", ") { (it as? IrTypeProjection)?.type?.let(::csType) ?: "object" } + ">"
	}

	/** The C# call target: the instance receiver expression, or a static `global::Type`. */
	private fun memberTarget(call: IrFunctionAccessExpression, clrType: String): String {
		val receiver = dispatchReceiverOf(call)
		return when {
			receiver != null && receiver !is IrGetObjectValue -> genExpr(receiver)
			clrType.isNotEmpty() -> "global::$clrType"
			receiver is IrGetObjectValue -> genExpr(receiver) // user object singleton -> Name.INSTANCE
			else -> "this"
		}
	}

	/** The Kotlin synthetic dispatch-receiver name `<this>` becomes C# `this`. */
	private fun valueName(name: String): String = if (name == "<this>") "this" else csId(name)

	// Synthetic temporaries (names with `<>`, e.g. `<destruct>`) share one name in the IR but distinct
	// symbols; give each symbol a unique, stable C# name so declaration and references agree without colliding.
	private val synthNames = HashMap<org.jetbrains.kotlin.ir.symbols.IrValueSymbol, String>()
	private var synthCounter = 0
	private fun localName(sym: org.jetbrains.kotlin.ir.symbols.IrValueSymbol): String {
		val raw = sym.owner.name.asString()
		if (raw == "<this>") return "this"
		if ('<' in raw || '>' in raw)
			return synthNames.getOrPut(sym) { "__t${synthCounter++}_${raw.replace("<", "").replace(">", "")}" }
		return csId(raw)
	}

	/** A nullable value type (`Int?`) — needs an explicit C# type since `var` can't infer it from `null`. */
	private fun nullableValueType(t: IrType): Boolean = t.isMarkedNullable() && t.classFqName?.asString() in PRIMITIVES.keys

	/** Kotlin `Array<T>` and the primitive arrays (`IntArray`…) — emitted as C# `T[]`. */
	private fun isArrayType(t: IrType): Boolean {
		val fq = t.classFqName?.asString()
		return fq == "kotlin.Array" || fq in PRIMITIVE_ARRAYS.keys
	}

	/** Kotlin collections / String with a C# indexer. */
	private fun isIndexableCollection(t: IrType): Boolean =
		t.classFqName?.asString() in setOf(
			"kotlin.collections.List", "kotlin.collections.MutableList",
			"kotlin.collections.Map", "kotlin.collections.MutableMap", "kotlin.String",
		)

	/** Types whose `==` is value equality in C# (primitives, enums-as-int, strings). */
	private fun isEqByValue(type: IrType): Boolean {
		val fq = type.classFqName?.asString()
		if (fq in PRIMITIVES || fq == "kotlin.String") return true
		return (type.classifierOrNull?.owner as? IrClass)?.kind == ClassKind.ENUM_CLASS
	}

	/** Escapes a Kotlin identifier that collides with a C# keyword (`out` -> `@out`). */
	private fun csId(name: String): String =
		if (name in CS_KEYWORDS) "@$name" else name.replace("<", "__").replace(">", "")   // <unary> -> __unary

	private fun dispatchReceiverOf(call: IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.DispatchReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	private fun extensionReceiverOf(call: IrFunctionAccessExpression): IrExpression? {
		val params = (call.symbol.owner as? IrFunction)?.parameters ?: return null
		val idx = params.indexOfFirst { it.kind == IrParameterKind.ExtensionReceiver }
		return if (idx in 0 until call.arguments.size) call.arguments[idx] else null
	}

	private fun hasAnnotation(decl: IrAnnotationContainer, fqName: String): Boolean =
		decl.annotations.any { (it as? IrConstructorCall)?.type?.classFqName?.asString() == fqName }

	/** Operands corresponding to `Regular` parameters only (drops dispatch/extension receivers). */
	private fun regularArgs(call: IrFunctionAccessExpression): List<IrExpression> {
		val params = (call.symbol.owner as? IrFunction)?.parameters ?: emptyList()
		return call.arguments.mapIndexedNotNull { i, arg ->
			if (arg != null && i < params.size && params[i].kind == IrParameterKind.Regular) arg else null
		}
	}

	/** Reads the `.NET` name from a `@Clr("...")` annotation, if present. */
	private fun clrName(decl: IrAnnotationContainer): String? {
		for (annotation in decl.annotations) {
			if ((annotation as? IrConstructorCall)?.type?.classFqName?.asString() == CLR_ANNOTATION) {
				return (annotation.arguments.firstOrNull() as? IrConst)?.value as? String
			}
		}
		// S5: types injected into FIR (no façade, no @Clr annotation) carry their .NET name in the registry.
		if (decl is IrClass) decl.fqNameWhenAvailable?.asString()?.let { clrc.ClrTypeRegistry.dotNetName(it)?.let { n -> return n } }
		return null
	}

	// New (2.2+) argument model: `arguments` is aligned to the callee's parameter list, with the
	// dispatch/extension receiver occupying the leading slots. filterNotNull drops absent defaults.
	private fun operandList(call: IrFunctionAccessExpression): List<IrExpression> =
		call.arguments.filterNotNull()

	private fun constLiteral(const: IrConst): String = when (val v = const.value) {
		is String -> "\"" + v.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n") + "\""
		is Boolean -> v.toString()
		is Char -> "'$v'"
		is Long -> "${v}L"
		null -> "null"
		else -> v.toString()
	}

	private fun csType(type: IrType): String {
		val fq = type.classFqName?.asString()
		// Nullable value types: `Int?` -> C# `int?` (reference types are already nullable without NRT).
		if (type.isMarkedNullable()) PRIMITIVES[fq]?.let { return "$it?" }
		// Arrays: `Array<T>` -> `T[]`, primitive arrays (`IntArray`…) -> `int[]` etc.
		PRIMITIVE_ARRAYS[fq]?.let { return it }
		if (fq == "kotlin.Array") {
			val elem = (type as? IrSimpleType)?.arguments?.firstOrNull()?.let { (it as? IrTypeProjection)?.type }
			return "${elem?.let(::csType) ?: "object"}[]"
		}
		// Pair<A,B>/Triple<A,B,C> -> C# value tuples `(A, B)` / `(A, B, C)` (.first->Item1 etc.).
		if (fq == "kotlin.Pair" || fq == "kotlin.Triple") {
			val args = (type as? IrSimpleType)?.arguments.orEmpty().map { (it as? IrTypeProjection)?.type?.let(::csType) ?: "object" }
			return "(${args.joinToString(", ")})"
		}
		// Primitives & well-known Kotlin types map to C# keywords first.
		PRIMITIVES[fq]?.let { return it }
		// Kotlin/JVM types (exceptions etc.) map to their .NET equivalents.
		NET_TYPES[fq]?.let { return "global::$it" }
		// Kotlin collections map to BCL generics.
		COLLECTIONS[fq]?.let { return "global::$it${csTypeArgs(type)}" }
		val klass = type.classifierOrNull?.owner as? IrClass
		if (klass != null) {
			// @Clr façade -> real .NET type; otherwise a user-declared class/object/interface.
			clrName(klass)?.let { return "global::$it${csTypeArgs(type)}" }
			return klass.name.asString() + csTypeArgs(type)
		}
		return "object"
	}

	private fun fileClassName(file: IrFile): String {
		val base = file.fileEntry.name.substringAfterLast('/').substringAfterLast('\\').removeSuffix(".kt")
		return base.replaceFirstChar { it.uppercaseChar() } + "Kt"
	}

	companion object {
		private const val CLR_ANNOTATION = "clr.Clr"
		private const val CLR_AWAIT = "clr.ClrAwait"
		private const val CLR_SM = "clr.Sm"

		private val BINARY_OPERATORS = mapOf(
			"plus" to "+", "minus" to "-", "times" to "*", "div" to "/", "rem" to "%",
			"less" to "<", "lessOrEqual" to "<=", "greater" to ">", "greaterOrEqual" to ">=",
			"EQEQ" to "==", "EQEQEQ" to "==", "ieee754equals" to "==",
			// Bitwise / shift infix functions (Int/Long/Boolean).
			"and" to "&", "or" to "|", "xor" to "^", "shl" to "<<", "shr" to ">>", "ushr" to ">>>",
		)

		private val UNARY_OPERATORS = mapOf(
			"unaryMinus" to "-", "unaryPlus" to "+", "not" to "!", "inv" to "~",
		)

		private val PRIMITIVES = mapOf(
			"kotlin.Unit" to "void", "kotlin.Nothing" to "void",
			"kotlin.Int" to "int", "kotlin.Long" to "long", "kotlin.Short" to "short",
			"kotlin.Byte" to "sbyte", "kotlin.Double" to "double", "kotlin.Float" to "float",
			"kotlin.Boolean" to "bool", "kotlin.Char" to "char", "kotlin.String" to "string",
			"kotlin.Any" to "object",
		)

		private val CS_KEYWORDS = setOf(
			"abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
			"class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
			"enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
			"foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
			"long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
			"private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
			"sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
			"try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
			"void", "volatile", "while", "lock",
		)

		private val COLLECTIONS = mapOf(
			"kotlin.collections.List" to "System.Collections.Generic.List",
			"kotlin.collections.MutableList" to "System.Collections.Generic.List",
			"kotlin.collections.Collection" to "System.Collections.Generic.List",
			"kotlin.collections.Iterable" to "System.Collections.Generic.IEnumerable",
			"kotlin.collections.Set" to "System.Collections.Generic.HashSet",
			"kotlin.collections.MutableSet" to "System.Collections.Generic.HashSet",
			"kotlin.collections.Map" to "System.Collections.Generic.Dictionary",
			"kotlin.collections.MutableMap" to "System.Collections.Generic.Dictionary",
		)

		private val LIST_FACTORIES = setOf(
			"kotlin.collections.listOf", "kotlin.collections.mutableListOf", "kotlin.collections.arrayListOf", "kotlin.collections.emptyList",
		)

		private val SET_FACTORIES = setOf(
			"kotlin.collections.setOf", "kotlin.collections.mutableSetOf", "kotlin.collections.hashSetOf", "kotlin.collections.emptySet",
		)

		private val MAP_FACTORIES = setOf(
			"kotlin.collections.mapOf", "kotlin.collections.mutableMapOf", "kotlin.collections.hashMapOf", "kotlin.collections.emptyMap",
		)

		private val COLLECTION_OPS = setOf(
			"map", "filter", "flatMap", "take", "drop", "sortedBy", "reversed", "forEach",
			"fold", "any", "all", "count", "sum", "first", "find",
			"reduce", "distinct", "sorted", "toSet", "toList", "maxOrNull", "minOrNull",
			"average", "contains", "isEmpty", "isNotEmpty", "joinToString",
			"firstOrNull", "lastOrNull", "last", "none", "single", "sumOf", "maxByOrNull", "minByOrNull",
			"groupBy", "associateBy", "associateWith", "zip",
		).mapTo(HashSet()) { "kotlin.collections.$it" }

		private val SCOPE_FUNCTIONS = setOf(
			"kotlin.let", "kotlin.run", "kotlin.apply", "kotlin.also", "kotlin.with",
		)

		private val MATH_FUNCS = mapOf(
			"abs" to "Abs", "max" to "Max", "min" to "Min", "sqrt" to "Sqrt", "pow" to "Pow",
			"round" to "Round", "floor" to "Floor", "ceil" to "Ceiling", "exp" to "Exp",
			"ln" to "Log", "log10" to "Log10", "sin" to "Sin", "cos" to "Cos", "tan" to "Tan",
		).mapKeys { "kotlin.math.${it.key}" }

		private val STRING_OPS = mapOf(
			"uppercase" to "ToUpper", "lowercase" to "ToLower", "trim" to "Trim",
			"trimStart" to "TrimStart", "trimEnd" to "TrimEnd", "substring" to "Substring",
			"replace" to "Replace", "startsWith" to "StartsWith", "endsWith" to "EndsWith",
			"contains" to "Contains", "indexOf" to "IndexOf", "padStart" to "PadLeft", "padEnd" to "PadRight",
		).mapKeys { "kotlin.text.${it.key}" }

		// Numeric conversions on a number receiver (`3.7.toInt()`) -> a C# cast `(int)3.7`.
		private val NUMBER_CONV = mapOf(
			"toInt" to "int", "toLong" to "long", "toDouble" to "double", "toFloat" to "float",
			"toShort" to "short", "toByte" to "sbyte", "toChar" to "char",
		)

		private val NUMBER_PARSE = mapOf(
			"toInt" to "global::System.Int32", "toLong" to "global::System.Int64",
			"toDouble" to "global::System.Double", "toFloat" to "global::System.Single",
			"toShort" to "global::System.Int16", "toByte" to "global::System.Byte",
		).mapKeys { "kotlin.text.${it.key}" }

		private val CHAR_OPS = mapOf(
			"isDigit" to "IsDigit", "isLetter" to "IsLetter", "isWhitespace" to "IsWhiteSpace",
			"isLetterOrDigit" to "IsLetterOrDigit", "uppercaseChar" to "ToUpper", "lowercaseChar" to "ToLower",
		).mapKeys { "kotlin.text.${it.key}" }

		private val ARRAY_FACTORIES = setOf(
			"kotlin.arrayOf", "kotlin.intArrayOf", "kotlin.longArrayOf", "kotlin.doubleArrayOf",
			"kotlin.floatArrayOf", "kotlin.booleanArrayOf", "kotlin.charArrayOf", "kotlin.byteArrayOf",
			"kotlin.shortArrayOf",
		)

		private val BUILTIN_PROPS = mapOf(
			"size" to "Count", "length" to "Length",
			"first" to "Item1", "second" to "Item2", "third" to "Item3",   // Pair/Triple -> ValueTuple
		)

		private val PRIMITIVE_ARRAYS = mapOf(
			"kotlin.IntArray" to "int[]", "kotlin.LongArray" to "long[]", "kotlin.DoubleArray" to "double[]",
			"kotlin.FloatArray" to "float[]", "kotlin.BooleanArray" to "bool[]", "kotlin.CharArray" to "char[]",
			"kotlin.ByteArray" to "byte[]", "kotlin.ShortArray" to "short[]",
		)

		// Kotlin Object-method names -> their C# (System.Object) equivalents.
		private val OBJECT_METHODS = mapOf("toString" to "ToString", "equals" to "Equals", "hashCode" to "GetHashCode")

		// Kotlin/JVM types (as surfaced by the reused JVM frontend) -> .NET equivalents.
		private val NET_TYPES = mapOf(
			"kotlin.AutoCloseable" to "System.IDisposable", "java.lang.AutoCloseable" to "System.IDisposable",
			"java.io.Closeable" to "System.IDisposable", "kotlin.io.Closeable" to "System.IDisposable",
			"java.lang.Object" to "System.Object", "java.lang.String" to "System.String",
			"java.lang.Throwable" to "System.Exception", "kotlin.Throwable" to "System.Exception",
			"java.lang.Exception" to "System.Exception", "kotlin.Exception" to "System.Exception",
			"java.lang.RuntimeException" to "System.Exception", "kotlin.RuntimeException" to "System.Exception",
			"java.lang.ArithmeticException" to "System.ArithmeticException",
			"java.lang.IllegalArgumentException" to "System.ArgumentException",
			"kotlin.IllegalArgumentException" to "System.ArgumentException",
			"java.lang.IllegalStateException" to "System.InvalidOperationException",
			"kotlin.IllegalStateException" to "System.InvalidOperationException",
			"java.lang.IndexOutOfBoundsException" to "System.IndexOutOfRangeException",
			"java.lang.NullPointerException" to "System.NullReferenceException",
			"java.lang.UnsupportedOperationException" to "System.NotSupportedException",
		)
	}
}

private fun org.jetbrains.kotlin.ir.declarations.IrDeclaration.fqNameOrNull(): String? =
	(this as? IrSimpleFunction)?.let { fn ->
		val parent = fn.parent
		val parentFq = (parent as? org.jetbrains.kotlin.ir.declarations.IrPackageFragment)?.packageFqName?.asString()
		if (parentFq != null) "$parentFq.${fn.name.asString()}" else null
	}
