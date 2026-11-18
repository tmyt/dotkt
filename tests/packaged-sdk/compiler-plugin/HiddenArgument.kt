package probe

import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.backend.common.lower.DeclarationIrBuilder
import org.jetbrains.kotlin.ir.builders.*
import org.jetbrains.kotlin.ir.builders.declarations.addValueParameter
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrExpression
import org.jetbrains.kotlin.ir.util.hasAnnotation
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.ir.visitors.*

/** Change both sides of a cross-module call, after each module's frontend has finished. */
@OptIn(org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)
fun lowerHiddenArgument(module: IrModuleFragment, context: IrPluginContext) {
    val marker = FqName("pluginprobe.api.Marker")
    fun target(fn: IrSimpleFunction) = fn.hasAnnotation(marker)
    fun addToken(fn: IrSimpleFunction) = fn.addValueParameter("extraToken", context.irBuiltIns.intType)
    fun lower(fn: IrSimpleFunction) {
        if (target(fn)) {
            val token = addToken(fn)
            fn.body = DeclarationIrBuilder(context, fn.symbol).irBlockBody { +irReturn(irGet(token)) }
        } else {
            fn.transformChildrenVoid(object : IrElementTransformerVoid() {
                override fun visitCall(expression: IrCall): IrExpression {
                    expression.transformChildrenVoid(this)
                    val callee = expression.symbol.owner
                    if (!target(callee)) return expression
                    check(callee.parameters.single { it.name.asString() == "content" }.type.hasAnnotation(marker))
                    val args = expression.arguments.toList()
                    if (callee.parameters.size == args.size) addToken(callee)
                    val builder = DeclarationIrBuilder(context, fn.symbol, expression.startOffset, expression.endOffset)
                    return builder.irCall(expression.symbol).also { call ->
                        expression.typeArguments.forEachIndexed { i, type -> call.typeArguments[i] = type }
                        args.forEachIndexed { i, arg -> call.arguments[i] = arg }
                        call.arguments[args.size] = builder.irInt(73)
                    }
                }
            })
        }
    }
    fun visit(container: IrDeclarationContainer) {
        for (declaration in container.declarations) when (declaration) {
            is IrClass -> visit(declaration)
            is IrSimpleFunction -> lower(declaration)
        }
    }
    module.files.forEach(::visit)
}
