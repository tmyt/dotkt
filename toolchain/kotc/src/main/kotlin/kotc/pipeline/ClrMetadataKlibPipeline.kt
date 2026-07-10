@file:Suppress("DEPRECATION")

package kotc.pipeline

import org.jetbrains.kotlin.builtins.DefaultBuiltIns
import org.jetbrains.kotlin.cli.common.metadataDestinationDirectory
import org.jetbrains.kotlin.cli.metadata.buildKotlinMetadataLibrary
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.PerformanceNotifications
import org.jetbrains.kotlin.cli.pipeline.PipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataSerializationArtifact
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.fir.backend.ConstValueProviderImpl
import org.jetbrains.kotlin.fir.backend.Fir2IrConfiguration
import org.jetbrains.kotlin.fir.backend.Fir2IrExtensions
import org.jetbrains.kotlin.fir.backend.Fir2IrVisibilityConverter
import org.jetbrains.kotlin.fir.backend.utils.extractFirDeclarations
import org.jetbrains.kotlin.fir.moduleData
import org.jetbrains.kotlin.fir.packageFqName
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.fir.pipeline.convertToIrAndActualize
import org.jetbrains.kotlin.fir.serialization.FirKLibSerializerExtension
import org.jetbrains.kotlin.fir.serialization.serializeSingleFirFile
import org.jetbrains.kotlin.ir.backend.js.lower.serialization.ir.JsManglerIr
import org.jetbrains.kotlin.ir.types.IrTypeSystemContextImpl
import org.jetbrains.kotlin.library.SerializedMetadata
import org.jetbrains.kotlin.library.metadata.KlibMetadataHeaderFlags
import org.jetbrains.kotlin.library.metadata.KlibMetadataProtoBuf

/**
 * The klib-build-only tail of the pipeline: run Fir2Ir on the fragment-actualized FIR (SAME
 * mechanism as [ClrCommonFir2IrPipelinePhase], just keeping the frontend artifact alongside so the
 * serializer below can still walk the ORIGINAL FirFiles) and then serialize a metadata KLIB.
 *
 * Why not reuse the stock `MetadataKlibSerializerPhase` as-is (task #80): that phase hardcodes
 * `constValueProvider = null` (verified against upstream/compiler/cli's
 * MetadataKlibSerializerPhase.kt — this is intentional there, since JVM/Native never resolve
 * `kotlin.*` constants FROM the metadata klib, they read the real per-target IR/klib). kotc's app
 * frontend, though, resolves the ENTIRE stdlib from this one metadata klib, so a `const val`
 * (`Int.MIN_VALUE`, `Double.POSITIVE_INFINITY`, ...) with no compiled value baked into the klib
 * metadata surfaces downstream as an IR-interpreter `InterpreterMethodNotFoundError`. Fir2Ir's own
 * actualization pipeline already const-folds the WHOLE module into `Fir2IrComponents.configuration
 * .evaluatedConstTracker` as a side effect of [convertToIrAndActualize] (see
 * upstream Fir2IrConverter.Companion.evaluateConstants, called from runActualizationPipeline) — we
 * just need to plug that same tracker into [ConstValueProviderImpl] and pass it to the serializer
 * instead of null.
 */
class ClrMetadataKlibFir2IrArtifact(
	val frontend: MetadataFrontendPipelineArtifact,
	val fir2IrResult: Fir2IrActualizedResult,
) : PipelineArtifact()

object ClrMetadataKlibFir2IrPhase : PipelinePhase<MetadataFrontendPipelineArtifact, ClrMetadataKlibFir2IrArtifact>(
	name = "ClrMetadataKlibFir2IrPhase",
	preActions = setOf(PerformanceNotifications.TranslationToIrStarted),
	postActions = setOf(PerformanceNotifications.TranslationToIrFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: MetadataFrontendPipelineArtifact): ClrMetadataKlibFir2IrArtifact {
		val fir2IrResult = input.result.convertToIrAndActualize(
			Fir2IrExtensions.Default,
			Fir2IrConfiguration.forKlibCompilation(input.configuration, input.diagnosticCollector),
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
	// Our pinned 2.2.0 compiler-embeddable doesn't have the newer KlibWritingStarted/Finished notifications
	// (present in a later upstream snapshot); BackendStarted/Finished is what the stock 2.2.0
	// MetadataKlibSerializerPhase bytecode actually uses for this same serialization step.
	preActions = setOf(PerformanceNotifications.BackendStarted),
	postActions = setOf(PerformanceNotifications.BackendFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: ClrMetadataKlibFir2IrArtifact): MetadataSerializationArtifact {
		val frontend = input.frontend
		val firResult = frontend.result
		val configuration = frontend.configuration
		val metadataVersion = frontend.metadataVersion
		val destDir = configuration.metadataDestinationDirectory!!
		val components = input.fir2IrResult.components

		// The real evaluated-const tracker, populated by convertToIrAndActualize's constant-folding pass —
		// see the class-doc comment above for why this (not null) is what makes const val initializers carry
		// a compiled VALUE into the klib.
		val constValueProvider = ConstValueProviderImpl(components)

		// Which `expect` declarations got ACTUALIZED by convertToIrAndActualize (common's `expect class
		// String`/`expect val POSITIVE_INFINITY` merged with the clr fragment's `actual`). Mirrors upstream's
		// Fir2KlibMetadataSerializer (used by Kotlin/Native's per-target klib serialization, the one HMPP-aware
		// serializer upstream ships) — WITHOUT this, `serializeSingleFirFile` has no way to tell "this expect
		// was actualized" from "this is a genuinely unactualized expect", so it emits BOTH the common
		// session's raw expect class (fake-override hashCode -> kotlin.Any) AND the clr session's actual class
		// as separate top-level `kotlin.String` declarations; a plain (non-HMPP) consumer session picks the
		// FIRST one it resolves, which is the unactualized expect (verified via the emitted IR: `hashCode
		// [expect,fake_override] declared in kotlin.String -> kotlin.Any`, not the real polynomial-hash body).
		val actualizedExpectDeclarations = input.fir2IrResult.irActualizedResult?.actualizedExpectDeclarations?.extractFirDeclarations()

		// ALSO mirroring Fir2KlibMetadataSerializer: serialize every FirFile against the SAME actualized
		// session/scopeSession/firProvider (components.*), not each file's own PRE-actualization fragment
		// session — the actualized session is what has the merged (single, real) view of each declaration.
		val languageVersionSettings = configuration.languageVersionSettings
		val fragments = mutableMapOf<String, MutableList<ByteArray>>()
		for (output in firResult.outputs) {
			for (firFile in output.fir) {
				val packageFragment = serializeSingleFirFile(
					firFile,
					components.session,
					components.scopeSession,
					actualizedExpectDeclarations,
					FirKLibSerializerExtension(
						components.session, components.scopeSession, components.firProvider, metadataVersion,
						constValueProvider = constValueProvider,
						exportKDoc = false,
						additionalMetadataProvider = null,
					),
					languageVersionSettings,
				)
				fragments.getOrPut(firFile.packageFqName.asString()) { mutableListOf() }.add(packageFragment.toByteArray())
			}
		}

		val header = KlibMetadataProtoBuf.Header.newBuilder()
		header.moduleName = firResult.outputs.last().session.moduleData.name.asString()
		if (configuration.languageVersionSettings.isPreRelease()) {
			header.flags = KlibMetadataHeaderFlags.PRE_RELEASE
		}

		val fragmentNames = mutableListOf<String>()
		val fragmentParts = mutableListOf<List<ByteArray>>()
		for ((fqName, fragment) in fragments.entries.sortedBy { it.key }) {
			fragmentNames += fqName
			fragmentParts += fragment
			header.addPackageFragmentName(fqName)
		}

		val module = header.build().toByteArray()
		val serializedMetadata = SerializedMetadata(module, fragmentParts, fragmentNames)
		buildKotlinMetadataLibrary(configuration, serializedMetadata, destDir)

		return MetadataSerializationArtifact(outputInfo = null, configuration, destDir.canonicalPath)
	}
}
