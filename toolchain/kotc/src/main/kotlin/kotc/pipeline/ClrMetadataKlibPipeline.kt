@file:Suppress("DEPRECATION")

package kotc.pipeline

import org.jetbrains.kotlin.builtins.DefaultBuiltIns
import org.jetbrains.kotlin.cli.common.diagnosticsCollector
import org.jetbrains.kotlin.cli.common.metadataDestinationDirectory
import org.jetbrains.kotlin.cli.metadata.buildKotlinMetadataLibrary
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.PerformanceNotifications
import org.jetbrains.kotlin.cli.pipeline.PipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataSerializationArtifact
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.fir.backend.Fir2IrConfiguration
import org.jetbrains.kotlin.fir.backend.Fir2IrExtensions
import org.jetbrains.kotlin.fir.backend.Fir2IrVisibilityConverter
import org.jetbrains.kotlin.fir.moduleData
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.fir.pipeline.Fir2KlibMetadataSerializer
import org.jetbrains.kotlin.fir.pipeline.convertToIrAndActualize
import org.jetbrains.kotlin.ir.backend.js.lower.serialization.ir.JsManglerIr
import org.jetbrains.kotlin.ir.types.IrTypeSystemContextImpl
import org.jetbrains.kotlin.library.SerializedMetadata
import org.jetbrains.kotlin.library.metadata.KlibMetadataHeaderFlags
import org.jetbrains.kotlin.library.metadata.KlibMetadataProtoBuf
import org.jetbrains.kotlin.util.klibMetadataVersionOrDefault

/**
 * The klib-build-only tail of the pipeline: run Fir2Ir on the fragment-actualized FIR (SAME
 * mechanism as [ClrCommonFir2IrPipelinePhase], just keeping the frontend artifact alongside so the
 * serializer below can walk the ORIGINAL FirFiles) and then serialize a metadata KLIB.
 *
 * Why not reuse the stock `MetadataKlib{InMemory,FileWriter}SerializerPhase` as-is: those phases pass
 * `fir2IrActualizedResult = null` (so `actualizedExpectDeclarations = null`) and serialize each FirFile
 * against its OWN pre-actualization fragment session. kotc's app frontend, though, resolves the ENTIRE
 * stdlib from this one metadata klib, so it needs the fragment-actualized SINGLE view: without it,
 * `serializeSingleFirFile` has no way to tell "this expect was actualized" from "genuinely unactualized
 * expect", and emits BOTH the common session's raw `expect class String` (fake-override hashCode ->
 * kotlin.Any) AND the clr session's `actual` as separate top-level `kotlin.String` declarations; a plain
 * (non-HMPP) consumer session then picks the unactualized expect. So this phase runs Fir2Ir first and
 * feeds its actualized result into upstream's [Fir2KlibMetadataSerializer] (2.4.0's HMPP/actualization-
 * aware serializer — exactly what kotc used to hand-roll), which serializes against the actualized
 * session and drops the actualized `expect`s.
 *
 * Const values (`Int.MIN_VALUE`, `Double.POSITIVE_INFINITY`, ...) are baked by FIR's own evaluator inside
 * `serializeSingleFirFile`; the old IR-interpreter `constValueProvider` fallback was removed in 2.4.0.
 */
class ClrMetadataKlibFir2IrArtifact(
	val frontend: MetadataFrontendPipelineArtifact,
	val fir2IrResult: Fir2IrActualizedResult,
) : PipelineArtifact() {
	override val configuration: CompilerConfiguration get() = frontend.configuration

	@OptIn(PipelineArtifact.CliPipelineInternals::class)
	override fun withCompilerConfiguration(newConfiguration: CompilerConfiguration): ClrMetadataKlibFir2IrArtifact =
		ClrMetadataKlibFir2IrArtifact(frontend.withCompilerConfiguration(newConfiguration), fir2IrResult)
}

object ClrMetadataKlibFir2IrPhase : PipelinePhase<MetadataFrontendPipelineArtifact, ClrMetadataKlibFir2IrArtifact>(
	name = "ClrMetadataKlibFir2IrPhase",
	preActions = setOf(PerformanceNotifications.TranslationToIrStarted),
	postActions = setOf(PerformanceNotifications.TranslationToIrFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: MetadataFrontendPipelineArtifact): ClrMetadataKlibFir2IrArtifact {
		val fir2IrResult = input.frontendOutput.convertToIrAndActualize(
			Fir2IrExtensions.Default,
			Fir2IrConfiguration.forKlibCompilation(input.configuration, input.configuration.diagnosticsCollector),
			irGeneratorExtensions = emptyList(),
			irMangler = JsManglerIr,
			visibilityConverter = Fir2IrVisibilityConverter.Default,
			kotlinBuiltIns = DefaultBuiltIns.Instance,
			typeSystemContextProvider = ::IrTypeSystemContextImpl,
			specialAnnotationsProvider = null,
			extraActualDeclarationExtractorsInitializer = { emptyList() },
		)
		return ClrMetadataKlibFir2IrArtifact(input, fir2IrResult)
	}
}

object ClrMetadataKlibSerializerPhase : PipelinePhase<ClrMetadataKlibFir2IrArtifact, MetadataSerializationArtifact>(
	name = "ClrMetadataKlibSerializerPhase",
	preActions = setOf(PerformanceNotifications.KlibWritingStarted),
	postActions = setOf(PerformanceNotifications.KlibWritingFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: ClrMetadataKlibFir2IrArtifact): MetadataSerializationArtifact {
		val frontend = input.frontend
		val configuration = frontend.configuration
		val destDir = configuration.metadataDestinationDirectory!!
		val metadataVersion = configuration.klibMetadataVersionOrDefault()

		// Upstream's actualization-aware serializer core: serializes each FirFile against the Fir2Ir-
		// actualized session (input.fir2IrResult) and drops actualized `expect` declarations, so the klib
		// carries one real view of each fragment-actualized type. Header/fragment assembly below mirrors the
		// stock MetadataKlibInMemorySerializerPhase.
		val serializer = Fir2KlibMetadataSerializer(
			configuration,
			frontend.frontendOutput.outputs,
			input.fir2IrResult,
			produceHeaderKlib = false,
		)

		val fragments = mutableMapOf<String, MutableList<ByteArray>>()
		serializer.forEachFile { _, _, firFile, _, packageFqName ->
			fragments.getOrPut(packageFqName.asString()) { mutableListOf() }
				.add(serializer.serializeSingleFileMetadata(firFile).toByteArray())
		}

		val header = KlibMetadataProtoBuf.Header.newBuilder()
		header.moduleName = frontend.frontendOutput.outputs.last().session.moduleData.name.asString()
		if (configuration.languageVersionSettings.isPreRelease()) {
			header.flags = KlibMetadataHeaderFlags.PRE_RELEASE
		}

		val fragmentNames = mutableListOf<String>()
		val fragmentParts = mutableListOf<List<ByteArray>>()
		for ((fqName, parts) in fragments.entries.sortedBy { it.key }) {
			fragmentNames += fqName
			fragmentParts += parts
			header.addPackageFragmentName(fqName)
		}

		val module = header.build().toByteArray()
		val serializedMetadata = SerializedMetadata(module, fragmentParts, fragmentNames, metadataVersion.toArray())
		buildKotlinMetadataLibrary(configuration, serializedMetadata, destDir)

		return MetadataSerializationArtifact(outputInfo = null, configuration, destDir.canonicalPath)
	}
}
