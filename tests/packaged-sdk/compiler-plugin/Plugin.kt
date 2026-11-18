@file:OptIn(org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class,
    org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)

package probe

import org.jetbrains.kotlin.GeneratedDeclarationKey
import org.jetbrains.kotlin.backend.common.extensions.IrGenerationExtension
import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.backend.common.lower.DeclarationIrBuilder
import org.jetbrains.kotlin.compiler.plugin.*
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.config.CompilerConfigurationKey
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.extensions.*
import org.jetbrains.kotlin.fir.plugin.createMemberFunction
import org.jetbrains.kotlin.fir.symbols.impl.FirClassSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.ir.builders.*
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.ir.IrElement
import org.jetbrains.kotlin.ir.expressions.IrCall
import org.jetbrains.kotlin.ir.expressions.IrConstructorCall
import org.jetbrains.kotlin.ir.visitors.*
import org.jetbrains.kotlin.name.*

private const val PLUGIN = "dotkt.fixture"
private val answer = CompilerConfigurationKey<List<String>>("fixture answer parts")
private val functionName = Name.identifier("generatedAnswer")
private val owner = ClassId.topLevel(FqName("fixture.Receiver"))
private object Key : GeneratedDeclarationKey()

class Options : CommandLineProcessor {
    override val pluginId = PLUGIN
    override val pluginOptions = listOf(CliOption("answer", "text", "value returned by generated code", required = true, allowMultipleOccurrences = true))
    override fun processOption(option: AbstractCliOption, value: String, configuration: CompilerConfiguration) {
        configuration.appendList(answer, value)
    }
}

private class Declarations(session: FirSession) : FirDeclarationGenerationExtension(session) {
    override fun getCallableNamesForClass(classSymbol: FirClassSymbol<*>, context: MemberGenerationContext) =
        if (classSymbol.classId == owner) setOf(functionName) else emptySet()

    override fun generateFunctions(callableId: CallableId, context: MemberGenerationContext?): List<FirNamedFunctionSymbol> {
        if (context == null || callableId.classId != owner || callableId.callableName != functionName) return emptyList()
        return listOf(createMemberFunction(context.owner, Key, functionName, session.builtinTypes.stringType.coneType).symbol)
    }
}

class Registrar : CompilerPluginRegistrar() {
    override val pluginId = PLUGIN
    override val supportsK2 = true
    override fun ExtensionStorage.registerExtensions(configuration: CompilerConfiguration) {
        val text = configuration.getNotNull(answer).joinToString("|")
        FirExtensionRegistrar.registerExtension(object : FirExtensionRegistrar() {
            override fun ExtensionRegistrarContext.configurePlugin() { +::Declarations }
        })
        IrGenerationExtension.registerExtension(object : IrGenerationExtension {
            override fun generate(moduleFragment: IrModuleFragment, pluginContext: IrPluginContext) {
                for (file in moduleFragment.files) for (klass in file.declarations.filterIsInstance<IrClass>())
                    for (fn in klass.declarations.filterIsInstance<IrSimpleFunction>()) {
                        if ((fn.origin as? IrDeclarationOrigin.GeneratedByPlugin)?.pluginKey != Key) continue
                        fn.body = DeclarationIrBuilder(pluginContext, fn.symbol).irBlockBody { +irReturn(irString(text)) }
                    }
                // Replace a source call without moving its debug span. The cached FIR
                // qualifier at that span belongs to Leaf.Method, not Middle.Method.
                // Model plugins that supply class arguments only on the constructed result type.
                moduleFragment.acceptChildrenVoid(object : IrVisitorVoid() {
                    override fun visitElement(element: IrElement) = element.acceptChildrenVoid(this)
                    override fun visitConstructorCall(expression: IrConstructorCall) {
                        expression.typeArguments.fill(null)
                        expression.acceptChildrenVoid(this)
                    }
                })
                val top = moduleFragment.files.flatMap { it.declarations.filterIsInstance<IrSimpleFunction>() }
                val original = top.singleOrNull { it.name.asString() == "original" } ?: return
                fun method(function: IrSimpleFunction): IrCall {
                    lateinit var call: IrCall
                    function.acceptVoid(object : IrVisitorVoid() {
                        override fun visitElement(element: IrElement) = element.acceptChildrenVoid(this)
                        override fun visitCall(expression: IrCall) {
                            if (expression.symbol.owner.name.asString() == "Method") call = expression
                            expression.acceptChildrenVoid(this)
                        }
                    })
                    return call
                }
                val from = method(original)
                val to = method(top.single { it.name.asString() == "replacement" })
                original.body = DeclarationIrBuilder(pluginContext, original.symbol, from.startOffset, from.endOffset)
                    .irBlockBody { +irReturn(irCall(to.symbol)) }
            }
        })
    }
}
