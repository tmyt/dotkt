package kotc.backend

import kotc.bir.TypeNode
import kotc.bir.BirSourcePoint
import kotc.bir.BirBreak
import kotc.bir.BirContinue
import kotc.bir.BirGoto
import kotc.bir.*
import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.ir.declarations.IrDeclaration
import org.jetbrains.kotlin.ir.declarations.IrDeclarationOrigin
import org.jetbrains.kotlin.ir.declarations.IrDeclarationWithVisibility
import org.jetbrains.kotlin.ir.declarations.IrAnonymousInitializer
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrConstructor
import org.jetbrains.kotlin.ir.declarations.IrFile
import org.jetbrains.kotlin.ir.declarations.IrFunction
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
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.expressions.IrReturn
import org.jetbrains.kotlin.ir.expressions.IrReturnableBlock
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

internal val hiddenSourcePoint = BirSourcePoint()

// One IR statement may project to several ordered source statements (e.g. a spliced return).
internal fun BirEmitter.stmt(node: org.jetbrains.kotlin.ir.IrElement): List<BirStatement> {
	val rendered = stmtCore(node)
	if (rendered.isEmpty()) return rendered
	val boundary = when (node) {
		// Branch/loop conditions are anchored after their labels by the CFG builders.
		is IrWhen, is IrWhileLoop, is IrDoWhileLoop, is IrComposite, is IrDeclaration,
		is IrInstanceInitializerCall -> false
		is IrBlock -> node.origin == IrStatementOrigin.FOR_LOOP
		is IrTypeOperatorCall -> node.operator != IrTypeOperator.IMPLICIT_COERCION_TO_UNIT
		else -> true
	}
	val source = when {
		boundary -> node
		node is IrVariable && node.origin == IrDeclarationOrigin.DEFINED -> node
		// An IR temporary can hold the first real evaluation of a subject/receiver/destructuring.
		// Attribute that evaluation to its expression, not to the generated local declaration.
		node is IrVariable && node.origin == IrDeclarationOrigin.IR_TEMPORARY_VARIABLE -> node.initializer
		else -> null
	}
	return source?.let { sourcePoint(it) }?.let { listOf(it) + rendered } ?: rendered
}

private fun BirEmitter.stmtCore(node: org.jetbrains.kotlin.ir.IrElement): List<BirStatement> = when (node) {
	// A `ClrRef<T>` delegate local (`var x by byref(m())`) -> a `ref T` local holding the live managed pointer
	// (byrefOf keeps the ref-return's pointer instead of deref'ing it). getValue/setValue inline to ldobj/stobj.
	is IrVariable -> listOf(if (birType(node.type) is TypeNode.ByRef) {
		val inner = node.initializer?.let { byrefMarker(it) ?: it }
		// A property read designates its own-source backing storage here just as it does when `byref(...)` is passed
		// directly to a ref parameter. Routing through get_<p>() would take the address of a returned copy, so writes
		// through the delegated local would be silently lost and a volatile backing field could not order ldobj/stobj.
		val init = inner?.let { BirByRefOf(inner = byrefBackingField(it) ?: expr(it)) }
		BirVariable(name = localSlotName(node), type = birType(node.type),
			init = init?.let { BirField.Value(it) } ?: BirField.Null)
	}
	// A ref-cell var: `var x = init` -> `val x = new dotkt$Ref_<elem>(init)` (the heap cell). The cell's `v` field —
	// and thus the synthesized ctor param — is the FULL element type `birType(node.type)`; when that is a value-type
	// nullable (`Int?` = `Nullable<T>`) the bare initializer (`= 5`, a `kotlin.Int`) must be wrapped to the `Nullable<T>`
	// the ctor expects. Carry the element type as the ctor's `argTypes` so ilemit coerces the arg (bare `T` -> `Nullable<T>`,
	// `null` -> `default(Nullable<T>)`) exactly as it coerces a plain-local var initializer / a `setField` value — without
	// it the `new Ref(5)` pushed a bare `int32` into a `Nullable<int32>` ctor slot -> InvalidProgram (#36).
	else if (isRefCell(node)) {
		val rt = refType(node)
		val elem = birType(node.type)
		val init = node.initializer?.let { expr(it) } ?: kotc.bir.BirDefaultValue(type = elem)
		BirVariable(name = localSlotName(node), type = rt,
			init = BirField.Value(BirNew(rt, listOf(init), argTypes = listOf(elem))))
	} else {
		// Evaluate the initializer FIRST so an object-expr init registers its synthetic name before the var's
		// type is read (`val x = object {}` whose type IS that anonymous class). A value-type-nullable initializer
		// (`Int?`) flowing into a non-null value slot (`val z: Int = n` after `if (n != null)`) is UNWRAPPED to
		// `Nullable<T>.Value` by coerceValue — the JVM `Integer.intValue()` coercion has no IR cast node (C1).
		val exactOuter = !node.isVar && node.initializer?.let {
			isExactOuterValue(it) && birType(node.type) == birType(it.type)
		} == true
		if (exactOuter) exactOuterValues[node] = true
		val init = node.initializer?.let { coerceValue(it, node.type) }
		// A `T?` (nullable type-parameter) LOCAL now carries its nullability on the `type` node itself
		// (`{t:nullable,of:tv}` from the uniform birType) — the decl-level `nullable` flag is RETIRED. bir2cir derives
		// the nullable-generic-local erasure (`type` -> object) from the type node.
		BirVariable(name = localSlotName(node), type = birType(node.type),
			init = init?.let { BirField.Value(it) } ?: BirField.Null, outer = true.takeIf { exactOuter })
	})
	// `val x by <delegate>` declared INSIDE a function (IrLocalDelegatedProperty): emit the delegate as a
	// local var; its getter/setter calls (`<get-x>`) are rewritten to delegate access in call() (localDelegates).
	is IrLocalDelegatedProperty -> {
		localDelegates[node.getter] = node
		node.setter?.let { localDelegates[it] = node }
		stmt(node.delegate!!)
	}
	// A ref-cell var write `x = e` -> `x.v = e` (through the shared heap cell, via the capture field inside a closure).
	is IrSetValue -> listOf(if (isRefCell(node.symbol.owner))
		BirSetField(refType(node.symbol.owner), refBase(node.symbol.owner), "v", expr(node.value))
	else BirSetLocal(captureLocalName[node.symbol.owner] ?: localSlotName(node.symbol.owner), expr(node.value)))
	is IrSetField -> {
		val ownerClass = node.symbol.owner.parent as? IrClass
		val clr = ownerClass?.let { clrName(it) }
		val receiver = node.receiver?.let { expr(it) } ?: BirThis()
		// #89: a STATIC backing field (top-level property -> file class; companion property -> enclosing class) ->
		// a `staticFieldSet`, NO receiver. Reached from the property's OWN custom setter body writing `field`.
		// `staticFieldSet` is a void EXPRESSION node (ilemit EmitExpr), so wrap it as an `exprStmt` in this
		// statement position (unlike the instance `setField`, which ilemit emits directly as a statement).
		val staticOwner = staticBackingFieldOwner(node.symbol.owner)
		val rendered = if (staticOwner != null) {
			val access = BirStaticFieldSet(TypeNode.Fqn(staticOwner), node.symbol.owner.name.asString(), expr(node.value))
			BirExpressionStatement(memberFieldVisibilityStamped(node.symbol.owner, access))
		} else {
			val ownerType = clr?.let { TypeNode.Fqn(it) } ?: ownerSpec(ownerClass, node.receiver?.type)
			val access = BirSetField(ownerType, receiver, node.symbol.owner.name.asString(), expr(node.value))
			memberFieldVisibilityStamped(node.symbol.owner, access)
		}
		listOf(rendered)
	}
	is IrReturn -> {
		val target = node.returnTargetSymbol.owner
		val retType = when (target) {
			is IrConstructor -> null
			is IrFunction -> target.returnType
			is IrReturnableBlock -> target.type
			else -> return listOf(BirExpressionStatement(invariantBroken(node,
				"IrReturn target ${target::class.simpleName} has no declared result type")))
		}
		// Unit is elided only for a Unit-returning TARGET. A Unit-typed value returned through Any? is a real
		// kotlin.Unit instance, not a void return. Constructors are physically void even though Kotlin IR exposes
		// their constructed class as `returnType`.
		val targetOmitsValue = target is IrConstructor || retType!!.isUnit()
		// A `return` inside a SPLICED inline body targeting the spliced fun/lambda must NOT emit a raw method
		// return — the splice is a valueBlock INSIDE the caller (a void caller got an Int32 on the stack at ret:
		// Duration.appendFractional splicing indexOfLast, ilverify ReturnVoid). Route to the splice's result
		// local + end label (see spliceBodyWithReturns). A non-spliced return keeps the plain shape below.
		val spliced = inlineReturnSubst[node.returnTargetSymbol]
		if (spliced != null) {
			val (res, end) = spliced
			val goto = BirGoto(end)
			// No return-site coerceValue/wrapNonNullValue here (unlike the non-spliced arm below): a spliced
			// `return@lambda <value-type-nullable>` into a bare-value result-local is well-typed only via a smart-cast,
			// which Fir2Ir materializes as a narrowed IrGetValue / IMPLICIT_CAST — already `nullableValue`-unwrapped by
			// expr()'s leaf arms — and a splice target is always a LAMBDA literal (never a postcondition-registered fn).
			// Verified a pure no-op across the value-nullable/smart-cast/generic battery (cases/il-inlineretcoerce).
			if (res != null) listOf(BirSetLocal(res, expr(node.value)), goto)
			// Unit splice: evaluate a side-effecting return value, then jump; a plain `return` just jumps.
			else if (node.value is IrGetObjectValue) listOf(goto)
			else listOf(BirExpressionStatement(expr(node.value)), goto)
		}
		// A return VALUE targeting Unit can still be a side-effecting expression — e.g. an expression-body
		// `fun main() = winUiApp { … }` or `return doCleanup()`. It must be EVALUATED, then a bare return; emitting
		// a bodyless `{"k":"return"}` would silently drop the call. A plain Unit reference
		// (`return` / `return Unit`, an IrGetObjectValue) has nothing to evaluate.
		// A value-type-nullable return value (`return n` where `n: Int?` is smart-cast, in a `: Int` function) must
		// UNWRAP `Nullable<T>.Value` to match the return slot — the JVM `Integer.intValue()` coercion has no IR node (C1).
		else if (!targetOmitsValue) {
			val valueRetType = retType
			val v0 = coerceValue(node.value, valueRetType)
			// #6 non-null RETURN POSTCONDITION: a genuine return targeting a registered public/protected fn wraps the
			// value in a bind-check-throw (skip a Nothing value — `return TODO()` already throws). Inline splices took
			// the branch above, so they never reach here.
			val postMsg = postconditionReturns[node.returnTargetSymbol]
			val v = if (postMsg != null && !node.value.type.isNothing()) wrapNonNullValue(v0, valueRetType, postMsg) else v0
			listOf(BirReturn(v))
		}
		else if (node.value is IrGetObjectValue) listOf(BirReturn())
		else listOf(BirExpressionStatement(expr(node.value)), BirReturn())
	}
	// E-0.5: `while`/`do-while` lower to a CFG block (label/brIf/goto) — the natural IL substrate; break/continue
	// inside become `goto` to the loop's break/continue label (incl. `break@outer`, matched by loop identity).
	// `for`/range stays structured (birForLoop) until §5.4; its break/continue fall to the structured nodes.
	is IrWhileLoop -> listOf(cfgWhile(node))
	is IrDoWhileLoop -> listOf(cfgDoWhile(node))
	// A break/continue targeting a CFG loop (on the stack) -> `goto` its label; otherwise (a structured
	// for-loop target) the structured node, which ilemit's loop stack resolves.
	is IrBreak -> listOf(cfgLoopStack.lastOrNull { it.first === node.loop }?.let { BirGoto(it.third) }
		?: BirBreak(node.label))
	is IrContinue -> listOf(cfgLoopStack.lastOrNull { it.first === node.loop }?.let { BirGoto(it.second) }
		?: BirContinue(node.label))
	is IrWhen -> listOf(cfgWhen(node))
	is IrTry -> listOf(tryStmt(node))
	is IrThrow -> listOf(BirThrow(expr(node.value)))
	// A value coerced to Unit in statement position (e.g. `i++`) -> emit its inner block as statements
	// (otherwise the side effects — the `<unary>` temp + the assignment — would be dropped).
	is IrTypeOperatorCall -> if (node.operator == IrTypeOperator.IMPLICIT_COERCION_TO_UNIT) {
		stmt(node.argument)
	} else listOf(BirExpressionStatement(expr(node)))
	// Preserve a local function as a lexical BIR declaration. bir2cir alone chooses its CLR MethodDef owner.
	is IrSimpleFunction -> listOf(localFunctionDecl(node))
	// A function-local class -> lift it to a top-level synthetic type (captures become leading ctor params).
	is IrClass -> listOf(liftLocalClass(node))
	is IrBlock -> listOf((if (node.origin?.toString() == "FOR_LOOP") birForLoop(node) else null)
		?: BirBlock(node.statements.flatMap { stmt(it) }))
	// IrComposite: a scope-less statement container (e.g. a desugared loop body) -> a flat block.
	is IrComposite -> listOf(BirBlock(node.statements.flatMap { stmt(it) }))
	is IrExpression -> listOf(BirExpressionStatement(expr(node)))
	else -> listOf(BirExpressionStatement(unsupported(node, "this statement", "the IR node ${node::class.simpleName} has no .NET lowering")))
}
