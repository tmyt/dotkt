package kotc.backend

import kotc.bir.TypeNode
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
import org.jetbrains.kotlin.ir.types.isNothing
import org.jetbrains.kotlin.ir.types.isMarkedNullable
import org.jetbrains.kotlin.ir.util.fqNameWhenAvailable
import org.jetbrains.kotlin.ir.IrFileEntry
import org.jetbrains.kotlin.cli.common.messages.MessageCollector
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity
import org.jetbrains.kotlin.cli.common.messages.CompilerMessageLocation
import java.io.File

// BIR statement rendering: an IR statement element -> BIR JSON (extension on BirEmitter).
internal fun BirEmitter.stmt(node: org.jetbrains.kotlin.ir.IrElement): String = when (node) {
	// A `ClrRef<T>` delegate local (`var x by byref(m())`) -> a `ref T` local holding the live managed pointer
	// (byrefOf keeps the ref-return's pointer instead of deref'ing it). getValue/setValue inline to ldobj/stobj.
	is IrVariable -> if (birType(node.type) is TypeNode.ByRef) {
		val inner = node.initializer?.let { byrefMarker(it) ?: it }
		// A property read designates its own-source backing storage here just as it does when `byref(...)` is passed
		// directly to a ref parameter. Routing through get_<p>() would take the address of a returned copy, so writes
		// through the delegated local would be silently lost and a volatile backing field could not order ldobj/stobj.
		val init = inner?.let { """{"k":"byrefOf","inner":${byrefBackingField(it) ?: expr(it)}}""" } ?: "null"
		"""{"k":"var","name":${str(localSlotName(node))},"type":${birType(node.type).toJson()},"init":$init}"""
	}
	// A ref-cell var: `var x = init` -> `val x = new dotkt$Ref_<elem>(init)` (the heap cell). The cell's `v` field —
	// and thus the synthesized ctor param — is the FULL element type `birType(node.type)`; when that is a value-type
	// nullable (`Int?` = `Nullable<T>`) the bare initializer (`= 5`, a `kotlin.Int`) must be wrapped to the `Nullable<T>`
	// the ctor expects. Carry the element type as the ctor's `argTypes` so ilemit coerces the arg (bare `T` -> `Nullable<T>`,
	// `null` -> `default(Nullable<T>)`) exactly as it coerces a plain-local var initializer / a `setField` value — without
	// it the `new Ref(5)` pushed a bare `int32` into a `Nullable<int32>` ctor slot -> InvalidProgram (#36).
	else if (isRefCell(node)) {
		val rt = refTypeName(node)
		val elem = birType(node.type)
		val init = node.initializer?.let { expr(it) } ?: """{"k":"default","type":${elem.toJson()}}"""
		"""{"k":"var","name":${str(localSlotName(node))},"type":${fqnJson(rt)},"init":{"k":"new","type":${fqnJson(rt)},"args":[$init],"argTypes":[${elem.toJson()}]}}"""
	} else {
		// Evaluate the initializer FIRST so an object-expr init registers its synthetic name before the var's
		// type is read (`val x = object {}` whose type IS that anonymous class). A value-type-nullable initializer
		// (`Int?`) flowing into a non-null value slot (`val z: Int = n` after `if (n != null)`) is UNWRAPPED to
		// `Nullable<T>.Value` by coerceValue — the JVM `Integer.intValue()` coercion has no IR cast node (C1).
		val init = node.initializer?.let { coerceValue(it, node.type) } ?: "null"
		// A `T?` (nullable type-parameter) LOCAL now carries its nullability on the `type` node itself
		// (`{t:nullable,of:tv}` from the uniform birType) — the decl-level `nullable` flag is RETIRED. bir2cir derives
		// the nullable-generic-local erasure (`type` -> object) from the type node.
		"""{"k":"var","name":${str(localSlotName(node))},"type":${birType(node.type).toJson()},"init":$init}"""
	}
	// `val x by <delegate>` declared INSIDE a function (IrLocalDelegatedProperty): emit the delegate as a
	// local var; its getter/setter calls (`<get-x>`) are rewritten to delegate access in call() (localDelegates).
	is IrLocalDelegatedProperty -> {
		localDelegates[node.getter] = node
		node.setter?.let { localDelegates[it] = node }
		stmt(node.delegate!!)
	}
	// A ref-cell var write `x = e` -> `x.v = e` (through the shared heap cell, via the capture field inside a closure).
	is IrSetValue -> if (isRefCell(node.symbol.owner))
		"""{"k":"setField","ownerType":${fqnJson(refTypeName(node.symbol.owner))},"recv":${refBase(node.symbol.owner)},"name":"v","value":${expr(node.value)}}"""
	else """{"k":"setLocal","name":${str(localSlotName(node.symbol.owner))},"value":${expr(node.value)}}"""
	is IrSetField -> {
		val ownerClass = node.symbol.owner.parent as? IrClass
		val clr = ownerClass?.let { clrName(it) }
		val recvJson = node.receiver?.let { expr(it) } ?: """{"k":"this"}"""
		// #89: a STATIC backing field (top-level property -> file class; companion property -> enclosing class) ->
		// a `staticFieldSet`, NO receiver. Reached from the property's OWN custom setter body writing `field`.
		// `staticFieldSet` is a void EXPRESSION node (ilemit EmitExpr), so wrap it as an `exprStmt` in this
		// statement position (unlike the instance `setField`, which ilemit emits directly as a statement).
		val staticOwner = staticBackingFieldOwner(node.symbol.owner)
		val rendered = if (staticOwner != null)
			"""{"k":"exprStmt","expr":{"k":"staticFieldSet","ownerType":${fqnJson(staticOwner)},"name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}}"""
		else if (clr != null)
			"""{"k":"setField","ownerType":${fqnJson(clr)},"recv":$recvJson,"name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		else
			"""{"k":"setField","ownerType":${ownerSpec(ownerClass, node.receiver?.type).toJson()},"recv":$recvJson,"name":${str(node.symbol.owner.name.asString())},"value":${expr(node.value)}}"""
		// staticFieldSet is wrapped in exprStmt at statement position; stamp the inner access node before wrapping.
		if (staticOwner != null) {
			val inner = rendered.removePrefix("{\"k\":\"exprStmt\",\"expr\":").dropLast(1)
			"""{"k":"exprStmt","expr":${memberFieldVisibilityStamped(node.symbol.owner, inner)}}"""
		} else memberFieldVisibilityStamped(node.symbol.owner, rendered)
	}
	is IrReturn -> {
		// A `return` inside a SPLICED inline body targeting the spliced fun/lambda must NOT emit a raw method
		// return — the splice is a valueBlock INSIDE the caller (a void caller got an Int32 on the stack at ret:
		// Duration.appendFractional splicing indexOfLast, ilverify ReturnVoid). Route to the splice's result
		// local + end label (see spliceBodyWithReturns). A non-spliced return keeps the plain shape below.
		val spliced = inlineReturnSubst[node.returnTargetSymbol]
		if (spliced != null) {
			val (res, end) = spliced
			val goto = """{"k":"goto","id":$end}"""
			// No return-site coerceValue/wrapReturnNonNull here (unlike the non-spliced arm below): a spliced
			// `return@lambda <value-type-nullable>` into a bare-value result-local is well-typed only via a smart-cast,
			// which Fir2Ir materializes as a narrowed IrGetValue / IMPLICIT_CAST — already `nullableValue`-unwrapped by
			// expr()'s leaf arms — and a splice target is always a LAMBDA literal (never a postcondition-registered fn).
			// Verified a pure no-op across the value-nullable/smart-cast/generic battery (cases/il-inlineretcoerce).
			if (res != null) """{"k":"setLocal","name":${str(res)},"value":${expr(node.value)}},$goto"""
			// Unit splice: evaluate a side-effecting return value, then jump; a plain `return` just jumps.
			else if (node.value is IrGetObjectValue) goto
			else """{"k":"exprStmt","expr":${expr(node.value)}},$goto"""
		}
		// A Unit-typed return VALUE can still be a side-effecting expression — e.g. an expression-body
		// `fun main() = winUiApp { … }` or `return doCleanup()`. It must be EVALUATED, then a bare return; emitting
		// a bare `{"k":"return"}` (the old behavior) silently dropped the call. A plain Unit reference
		// (`return` / `return Unit`, an IrGetObjectValue) has nothing to evaluate.
		// A value-type-nullable return value (`return n` where `n: Int?` is smart-cast, in a `: Int` function) must
		// UNWRAP `Nullable<T>.Value` to match the return slot — the JVM `Integer.intValue()` coercion has no IR node (C1).
		else if (!node.value.type.isUnit()) {
			val retType = (node.returnTargetSymbol.owner as? org.jetbrains.kotlin.ir.declarations.IrFunction)?.returnType
			val v0 = if (retType != null) coerceValue(node.value, retType) else expr(node.value)
			// #6 non-null RETURN POSTCONDITION: a genuine return targeting a registered public/protected fn wraps the
			// value in a bind-check-throw (skip a Nothing value — `return TODO()` already throws). Inline splices took
			// the branch above, so they never reach here.
			val postMsg = postconditionReturns[node.returnTargetSymbol]
			val v = if (postMsg != null && retType != null && !node.value.type.isNothing()) wrapReturnNonNull(v0, retType, postMsg) else v0
			"""{"k":"return","value":$v}"""
		}
		else if (node.value is IrGetObjectValue) """{"k":"return"}"""
		else """{"k":"exprStmt","expr":${expr(node.value)}},{"k":"return"}"""
	}
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
	// Preserve a local function as a lexical BIR declaration. bir2cir alone chooses its CLR MethodDef owner.
	is IrSimpleFunction -> localFunctionDecl(node)
	// A function-local class -> lift it to a top-level synthetic type (captures become leading ctor params).
	is IrClass -> liftLocalClass(node)
	is IrBlock -> (if (node.origin?.toString() == "FOR_LOOP") birForLoop(node) else null)
		?: """{"k":"block","body":[${node.statements.joinToString(",") { stmt(it) }}]}"""
	// IrComposite: a scope-less statement container (e.g. a desugared loop body) -> a flat block.
	is IrComposite -> """{"k":"block","body":[${node.statements.joinToString(",") { stmt(it) }}]}"""
	is IrExpression -> """{"k":"exprStmt","expr":${expr(node)}}"""
	else -> unsupported(node, "this statement", "the IR node ${node::class.simpleName} has no .NET lowering")
}
