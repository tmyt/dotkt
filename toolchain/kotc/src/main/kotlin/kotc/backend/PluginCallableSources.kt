@file:OptIn(org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)

package kotc.backend

import kotc.bir.SourceCallable
import org.jetbrains.kotlin.backend.common.extensions.IrGenerationExtension
import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.fir.backend.FirMetadataSource
import org.jetbrains.kotlin.fir.declarations.FirClass
import org.jetbrains.kotlin.fir.declarations.FirNamedFunction
import org.jetbrains.kotlin.fir.resolve.providers.firProvider
import org.jetbrains.kotlin.fir.serialization.FirElementSerializer
import org.jetbrains.kotlin.fir.serialization.FirKLibSerializerExtension
import org.jetbrains.kotlin.fir.serialization.TypeApproximatorForMetadataSerializer
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.ir.types.IrType
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.TypeTable
import org.jetbrains.kotlin.util.metadataVersion

/** The FIR declaration owns the source signature; the transformed IR declaration owns the call target's ID. */
class PluginCallableSources internal constructor(private val frontend: MetadataFrontendPipelineArtifact) : IrGenerationExtension {
    private data class Parameter(val kind: IrParameterKind, val name: String, val type: IrType,
        val vararg: IrType?, val hasDefault: Boolean)
    private data class Shape(val name: String, val result: IrType, val parameters: List<Parameter>,
        val typeParameters: List<List<IrType>>)
    private data class Original(val shape: Shape, val owners: List<FirClass>)
    private val originals = mutableMapOf<FirNamedFunction, Original>()

    private fun shape(fn: IrSimpleFunction) = Shape(fn.name.asString(), fn.returnType,
        fn.parameters.map { Parameter(it.kind, it.name.asString(), it.type, it.varargElementType, it.defaultValue != null) },
        fn.typeParameters.map { it.superTypes.toList() })

    private fun visit(container: IrDeclarationContainer, owners: List<FirClass>, action: (IrSimpleFunction, FirNamedFunction, List<FirClass>) -> Unit) {
        for (decl in container.declarations) when (decl) {
            is IrSimpleFunction -> if (!decl.isFakeOverride) {
                val source = (decl.metadata as? FirMetadataSource.Function)?.fir as? FirNamedFunction
                if (source != null) action(decl, source, owners)
            }
            is IrClass -> {
                val owner = (decl.metadata as? FirMetadataSource.Class)?.fir as? FirClass
                visit(decl, if (owner == null) owners else owners + owner, action)
            }
        }
    }

    override fun generate(moduleFragment: IrModuleFragment, pluginContext: IrPluginContext) {
        for (file in moduleFragment.files) visit(file, emptyList()) { fn, source, owners ->
            originals[source] = Original(shape(fn), owners)
        }
    }

    internal fun collect(file: IrFile, id: (IrSimpleFunction) -> String?): List<SourceCallable> = buildList {
        visit(file, emptyList()) { fn, source, _ ->
            val original = originals[source] ?: return@visit
            if (shape(fn) == original.shape) return@visit
            val target = id(fn) ?: return@visit
            val module = frontend.frontendOutput.outputs.single { it.session === source.moduleData.session }
            val session = module.session
            val configuration = frontend.configuration
            val extension = FirKLibSerializerExtension(session, module.scopeSession, session.firProvider,
                configuration.metadataVersion(), false, null)
            val approximator = TypeApproximatorForMetadataSerializer(session)
            val settings = configuration.languageVersionSettings
            val serializer = original.owners.fold(FirElementSerializer.createTopLevel(session, module.scopeSession,
                extension, approximator, settings, false)) { parent, owner ->
                FirElementSerializer.create(session, module.scopeSession, owner, extension, parent, approximator, settings, false)
            }
            val proto = serializer.functionProto(source)?.build() ?: return@visit
            val (strings, classifiers) = extension.stringTable.buildProto()
            val types = if (proto.hasTypeTable()) proto.typeTable else serializer.typeTable.serialize() ?: ProtoBuf.TypeTable.getDefaultInstance()
            add(TypeAliasDeclarations.Projection(NameResolverImpl(strings, classifiers)).callable(target, proto, TypeTable(types)))
        }
    }
}
