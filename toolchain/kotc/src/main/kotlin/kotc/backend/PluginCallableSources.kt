@file:OptIn(org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)

package kotc.backend

import kotc.bir.SourceCallable
import org.jetbrains.kotlin.backend.common.extensions.IrGenerationExtension
import org.jetbrains.kotlin.backend.common.extensions.IrPluginContext
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.fir.backend.FirMetadataSource
import org.jetbrains.kotlin.fir.declarations.FirClass
import org.jetbrains.kotlin.fir.declarations.FirCallableDeclaration
import org.jetbrains.kotlin.fir.declarations.FirNamedFunction
import org.jetbrains.kotlin.fir.declarations.FirProperty
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.fir.resolve.providers.firProvider
import org.jetbrains.kotlin.fir.serialization.FirElementSerializer
import org.jetbrains.kotlin.fir.serialization.FirKLibSerializerExtension
import org.jetbrains.kotlin.fir.serialization.TypeApproximatorForMetadataSerializer
import org.jetbrains.kotlin.ir.declarations.*
import org.jetbrains.kotlin.metadata.ProtoBuf
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl
import org.jetbrains.kotlin.metadata.deserialization.TypeTable
import org.jetbrains.kotlin.util.metadataVersion

/** Join the frontend signature to its physical function across the IR plugin boundary. */
class PluginCallableSources internal constructor(private val frontend: MetadataFrontendPipelineArtifact) : IrGenerationExtension {
    private data class Original(val function: IrSimpleFunction, val owners: List<FirClass>)
    private val originals = mutableMapOf<FirCallableDeclaration, Original>()

    private fun IrSimpleFunction.fir(): FirCallableDeclaration? {
        val property = correspondingPropertySymbol?.owner
        return if (property?.getter === this) (property.metadata as? FirMetadataSource.Property)?.fir
            else (metadata as? FirMetadataSource.Function)?.fir as? FirNamedFunction
    }

    private fun visit(container: IrDeclarationContainer, owners: List<FirClass>, action: (IrSimpleFunction, List<FirClass>) -> Unit) {
        for (decl in container.declarations) {
            if (decl is IrDeclarationWithVisibility && Visibilities.isPrivate(decl.visibility.delegate)) continue
            when (decl) {
                is IrSimpleFunction -> if (!decl.isFakeOverride) action(decl, owners)
                is IrProperty -> decl.getter?.takeUnless { it.isFakeOverride || Visibilities.isPrivate(it.visibility.delegate) }
                    ?.let { action(it, owners) }
                is IrClass -> {
                    val owner = (decl.metadata as? FirMetadataSource.Class)?.fir as? FirClass
                    // Static nesting starts a fresh generic frame; only inner classes capture enclosing parameters.
                    visit(decl, when {
                        owner == null -> owners
                        decl.isInner -> owners + owner
                        else -> listOf(owner)
                    }, action)
                }
            }
        }
    }

    override fun generate(moduleFragment: IrModuleFragment, pluginContext: IrPluginContext) {
        for (file in moduleFragment.files) visit(file, emptyList()) { fn, owners ->
            fn.fir()?.let { originals[it] = Original(fn, owners) }
        }
    }

    internal fun select(result: Fir2IrActualizedResult): (IrFile, (IrSimpleFunction) -> String?) -> List<SourceCallable> {
        val live = mutableMapOf<IrSimpleFunction, IrFile>()
        val representatives = mutableMapOf<FirCallableDeclaration, MutableList<IrSimpleFunction>>()
        for (file in result.irModuleFragment.files) visit(file, emptyList()) { fn, _ ->
            live[fn] = file
            fn.fir()?.let { representatives.getOrPut(it) { mutableListOf() }.add(fn) }
        }
        val selected = originals.mapNotNull { (source, original) ->
            val candidates = representatives[source].orEmpty()
            val fn = when {
                original.function in candidates -> original.function // A helper may have copied its metadata.
                candidates.size == 1 -> candidates.single() // A replacement may have moved the metadata.
                candidates.isEmpty() -> original.function.takeIf { it in live } ?: return@mapNotNull null
                else -> error("Multiple IR replacements claim the Kotlin declaration ${source.symbol}")
            }
            Triple(fn, source, original.owners)
        }.groupBy { (fn, _, _) -> live.getValue(fn) }

        // A plugin can change a type-use annotation without changing IrType equality. FIR (including
        // registered metadata annotations), not a comparison of IR shapes, owns the projected signature.
        val additional = result.components.annotationsFromPluginRegistrar.createAdditionalMetadataProvider()
        return { file, id -> selected[file].orEmpty().mapNotNull { (fn, source, owners) ->
            // Intrinsics and other vocabulary without an allocated MethodDef aren't KLIB call targets.
            val target = id(fn) ?: return@mapNotNull null
            val module = frontend.frontendOutput.outputs.single { it.session === source.moduleData.session }
            val session = module.session
            val configuration = frontend.configuration
            val extension = FirKLibSerializerExtension(session, module.scopeSession, session.firProvider,
                configuration.metadataVersion(), false, additional)
            val approximator = TypeApproximatorForMetadataSerializer(session)
            val settings = configuration.languageVersionSettings
            val serializer = owners.fold(FirElementSerializer.createTopLevel(session, module.scopeSession,
                extension, approximator, settings, false)) { parent, owner ->
                FirElementSerializer.create(session, module.scopeSession, owner, extension, parent, approximator, settings, false)
            }
            fun projection(): TypeAliasDeclarations.Projection {
                val (strings, classifiers) = extension.stringTable.buildProto()
                return TypeAliasDeclarations.Projection(NameResolverImpl(strings, classifiers))
            }
            fun types(table: ProtoBuf.TypeTable? = null) =
                TypeTable(table ?: serializer.typeTable.serialize() ?: ProtoBuf.TypeTable.getDefaultInstance())
            when (source) {
                is FirNamedFunction -> {
                    val proto = checkNotNull(serializer.functionProto(source)).build()
                    projection().callable(target, proto, types(proto.typeTable.takeIf { proto.hasTypeTable() }))
                }
                is FirProperty -> {
                    val proto = checkNotNull(serializer.propertyProto(source)).build()
                    projection().property(target, proto, types())
                }
                else -> error("Unexpected source callable ${source.symbol}")
            }
        } }
    }
}
