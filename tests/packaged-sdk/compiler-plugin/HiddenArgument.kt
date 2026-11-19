package probe

import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.backend.common.lower.DeclarationIrBuilder
import org.jetbrains.kotlin.ir.builders.*
import org.jetbrains.kotlin.ir.builders.declarations.addValueParameter
import org.jetbrains.kotlin.ir.builders.declarations.buildFun
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrBlockBody
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.expressions.IrStatementOrigin
import org.jetbrains.kotlin.ir.expressions.IrTypeOperator
import org.jetbrains.kotlin.ir.util.hasAnnotation
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.ir.visitors.*

/** Change both sides of a cross-module call, after each module's frontend has finished. */
@OptIn(org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)
fun lowerHiddenArgument(module: IrModuleFragment, context: IrPluginContext) {
    val marker = FqName("pluginprobe.api.Marker")
    fun target(fn: IrSimpleFunction) = fn.hasAnnotation(marker)
    fun addToken(fn: IrSimpleFunction) = fn.addValueParameter("extraToken", context.irBuiltIns.intType)
    fun lower(fn: IrSimpleFunction) {
        if (fn.name.asString() == "withCompositeLocal") {
            val body = (fn.body as IrBlockBody).statements
            val slot = body.removeAt(0) as IrVariable
            val call = body.first() as IrCall
            val argument = call.arguments.single()!!
            call.arguments[0] = DeclarationIrBuilder(context, fn.symbol).irComposite(resultType = argument.type) {
                +slot
                +argument
            }
        }
        if (target(fn)) {
            val token = addToken(fn)
            fn.body = DeclarationIrBuilder(context, fn.symbol).irBlockBody { +irReturn(irGet(token)) }
            // The original declaration can survive even when a plugin drops its metadata.
            if (fn.parent is IrClass) fn.metadata = null
        } else {
            fn.transformChildrenVoid(object : IrElementTransformerVoid() {
                override fun visitCall(expression: IrCall): IrExpression {
                    expression.transformChildrenVoid(this)
                    val callee = expression.symbol.owner
                    if (callee.name.asString() == "compositeDiscard") {
                        val builder = DeclarationIrBuilder(context, fn.symbol, expression.startOffset, expression.endOffset)
                        return builder.irComposite(resultType = expression.type) {
                            val unit = context.irBuiltIns.unitType
                            +typeOperator(unit, expression.arguments.single()!!, IrTypeOperator.IMPLICIT_COERCION_TO_UNIT, unit)
                        }
                    }
                    if (!target(callee)) return expression
                    if (callee.correspondingPropertySymbol == null)
                        check(callee.parameters.single { it.name.asString() == "content" }.type.hasAnnotation(marker))
                    val args = expression.arguments.toList()
                    if (callee.parameters.none { it.name.asString() == "extraToken" }) {
                        // fir2ir omits the receiver from a companion-extension call but leaves it
                        // in the deserialized stub. Rebuild the same physical signature as the producer.
                        if (callee.parameters.size == args.size + 1 &&
                            callee.parameters.first().kind == IrParameterKind.ExtensionReceiver)
                            callee.parameters = callee.parameters.drop(1)
                        addToken(callee)
                    }
                    val builder = DeclarationIrBuilder(context, fn.symbol, expression.startOffset, expression.endOffset)
                    return builder.irCall(expression.symbol).also { call ->
                        expression.typeArguments.forEachIndexed { i, type -> call.typeArguments[i] = type }
                        args.forEachIndexed { i, arg -> call.arguments[i] = arg }
                        // The consuming call must see the last value and execute the prefix.
                        call.arguments[args.size] = builder.irComposite(resultType = context.irBuiltIns.intType) {
                            val token = irTemporary(irComposite(origin = IrStatementOrigin.DEFAULT_VALUE,
                                resultType = context.irBuiltIns.intType) { +irInt(73) })
                            +irGet(token)
                        }
                    }
                }
            })
        }
    }
    fun visit(container: IrDeclarationContainer) {
        for (declaration in container.declarations) when (declaration) {
            is IrClass -> visit(declaration)
            is IrSimpleFunction -> lower(declaration)
                is IrProperty -> declaration.getter?.let(::lower)
        }
    }
    module.files.forEach(::visit)
    for (file in module.files) {
        val source = file.declarations.filterIsInstance<IrSimpleFunction>().firstOrNull(::target) ?: continue
        // Copying metadata onto a new IR helper must not give a second function the source signature.
        val helper = context.irFactory.buildFun {
            name = Name.identifier("physicalSibling")
            returnType = context.irBuiltIns.intType
        }.apply {
            parent = file
            metadata = source.metadata
            body = DeclarationIrBuilder(context, symbol).irBlockBody { +irReturn(irInt(0)) }
        }
        file.declarations += helper
    }
}
