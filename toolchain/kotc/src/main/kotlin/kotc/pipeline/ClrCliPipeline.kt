@file:OptIn(org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class)

package kotc.pipeline

import kotc.backend.ClrBackendPhase
import kotc.frontend.ClrCompilerPluginRegistrar
import org.jetbrains.kotlin.backend.common.phaser.then
import org.jetbrains.kotlin.builtins.DefaultBuiltIns
import org.jetbrains.kotlin.cli.common.CLIConfigurationKeys
import org.jetbrains.kotlin.cli.common.arguments.K2MetadataCompilerArguments
import org.jetbrains.kotlin.cli.common.config.addKotlinSourceRoot
import org.jetbrains.kotlin.cli.jvm.config.K2MetadataConfigurationKeys
import org.jetbrains.kotlin.cli.jvm.config.addJvmClasspathRoots
import org.jetbrains.kotlin.cli.pipeline.AbstractCliPipeline
import org.jetbrains.kotlin.cli.pipeline.AbstractConfigurationPhase
import org.jetbrains.kotlin.cli.pipeline.ArgumentsPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.ConfigurationUpdater
import org.jetbrains.kotlin.cli.pipeline.ConfigurationPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.Fir2IrPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PipelineContext
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.PerformanceNotifications
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.compiler.plugin.CompilerPluginRegistrar
import org.jetbrains.kotlin.config.CommonConfigurationKeys
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.config.moduleName
import org.jetbrains.kotlin.config.phaser.CompilerPhase
import org.jetbrains.kotlin.fir.backend.Fir2IrConfiguration
import org.jetbrains.kotlin.fir.backend.Fir2IrExtensions
import org.jetbrains.kotlin.fir.backend.Fir2IrVisibilityConverter
import org.jetbrains.kotlin.fir.pipeline.Fir2IrActualizedResult
import org.jetbrains.kotlin.fir.pipeline.convertToIrAndActualize
import org.jetbrains.kotlin.ir.backend.js.lower.serialization.ir.JsManglerIr
import org.jetbrains.kotlin.ir.types.IrTypeSystemContextImpl
import org.jetbrains.kotlin.metadata.deserialization.BinaryVersion
import org.jetbrains.kotlin.metadata.deserialization.MetadataVersion
import org.jetbrains.kotlin.metadata.jvm.deserialization.JvmProtoBufUtil
import org.jetbrains.kotlin.util.PerformanceManager
import java.io.File

/**
 * Registers our CLR FIR extensions into the common frontend by adding a compiler-plugin
 * registrar to the configuration, after it is built and before the frontend creates the session.
 * The frontend picks up
 * `COMPILER_PLUGIN_REGISTRARS` when it sets up the project, and our registrar installs the FIR
 * type-injection extension (façade-free `import System.*`).
 */
object ClrPluginRegistrationPhase : PipelinePhase<ConfigurationPipelineArtifact, ConfigurationPipelineArtifact>(
	name = "ClrPluginRegistration",
) {
	override fun executePhase(input: ConfigurationPipelineArtifact): ConfigurationPipelineArtifact {
		input.configuration.add(CompilerPluginRegistrar.COMPILER_PLUGIN_REGISTRARS, ClrCompilerPluginRegistrar())
		return input
	}
}

object ClrMetadataConfigurationPipelinePhase : AbstractConfigurationPhase<K2MetadataCompilerArguments>(
	name = "ClrMetadataConfigurationPipelinePhase",
	postActions = setOf(CheckCompilationErrors.CheckMessageCollector),
	configurationUpdaters = listOf(ClrMetadataConfigurationUpdater),
) {
	override fun createMetadataVersion(versionArray: IntArray): BinaryVersion = MetadataVersion(*versionArray)
}

object ClrMetadataConfigurationUpdater : ConfigurationUpdater<K2MetadataCompilerArguments>() {
	override fun fillConfiguration(
		input: ArgumentsPipelineArtifact<K2MetadataCompilerArguments>,
		configuration: CompilerConfiguration,
	) {
		val arguments = input.arguments
		val commonSources = arguments.commonSources?.toSet() ?: emptySet()
		val hmppModuleStructure = configuration.get(CommonConfigurationKeys.HMPP_MODULE_STRUCTURE)
		for (arg in arguments.freeArgs) {
			val moduleName = hmppModuleStructure?.modules?.firstOrNull { arg in it.sources }?.name
			configuration.addKotlinSourceRoot(arg, isCommon = arg in commonSources, hmppModuleName = moduleName)
		}
		arguments.classpath?.let { cp ->
			configuration.addJvmClasspathRoots(cp.split(File.pathSeparatorChar).map(::File))
		}
		configuration.moduleName = arguments.moduleName ?: JvmProtoBufUtil.DEFAULT_MODULE_NAME
		configuration.put(CLIConfigurationKeys.ALLOW_KOTLIN_PACKAGE, arguments.allowKotlinPackage)
		configuration.put(CLIConfigurationKeys.RENDER_DIAGNOSTIC_INTERNAL_NAME, arguments.renderInternalDiagnosticNames)
		configuration.putIfNotNull(K2MetadataConfigurationKeys.FRIEND_PATHS, arguments.friendPaths?.toList())
		configuration.putIfNotNull(K2MetadataConfigurationKeys.REFINES_PATHS, arguments.refinesPaths?.toList())
		arguments.destination?.let {
			configuration.put(CLIConfigurationKeys.METADATA_DESTINATION_DIRECTORY, File(it))
		} ?: configuration.getNotNull(CommonConfigurationKeys.MESSAGE_COLLECTOR_KEY).report(
			org.jetbrains.kotlin.cli.common.messages.CompilerMessageSeverity.ERROR,
			"Specify destination via -d",
		)
	}
}

class ClrFir2IrPipelineArtifact(
	override val result: Fir2IrActualizedResult,
	val configuration: org.jetbrains.kotlin.config.CompilerConfiguration,
	val diagnosticCollector: org.jetbrains.kotlin.diagnostics.impl.BaseDiagnosticsCollector,
) : Fir2IrPipelineArtifact()

object ClrCommonFir2IrPipelinePhase : PipelinePhase<MetadataFrontendPipelineArtifact, ClrFir2IrPipelineArtifact>(
	name = "ClrCommonFir2IrPipelinePhase",
	preActions = setOf(PerformanceNotifications.TranslationToIrStarted),
	postActions = setOf(PerformanceNotifications.TranslationToIrFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: MetadataFrontendPipelineArtifact): ClrFir2IrPipelineArtifact {
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
		return ClrFir2IrPipelineArtifact(fir2IrResult, input.configuration, input.diagnosticCollector)
	}
}

/**
 * Kotlin/CLR compiler driver.
 *
 * We reuse the official common metadata frontend construction (`prepareMetadataSessions` /
 * `prepareNativeSessions`, both public CLI API), run FIR2IR explicitly for KLIB/common
 * dependencies, and swap only the final backend phase for our own. [ClrAppFrontendPipelinePhase]
 * forks the thin CLI glue around `prepareMetadataSessions` (not Kotlin-core session-construction
 * logic) solely to install `kotlin.jvm.*` as a default import before any FIR resolution runs — see
 * `ClrDefaultImports.kt`.
 */
class ClrCliPipeline(
	override val defaultPerformanceManager: PerformanceManager,
) : AbstractCliPipeline<K2MetadataCompilerArguments>() {
	override fun createCompoundPhase(
		arguments: K2MetadataCompilerArguments,
	): CompilerPhase<PipelineContext, ArgumentsPipelineArtifact<K2MetadataCompilerArguments>, *> =
		when {
			// Build the CLR frontend stdlib KLIB itself: common+clr fragment-actualized FIR -> Fir2Ir (for
			// real constant folding) -> metadata klib. See ClrMetadataKlibPipeline.kt for why this needs its
			// own serializer rather than the stock MetadataKlibSerializerPhase.
			System.getenv("DOTKT_BUILD_KLIB") != null ->
				ClrMetadataConfigurationPipelinePhase then
					ClrStdlibFrontendPipelinePhase then
					ClrMetadataKlibFir2IrPhase then
					ClrMetadataKlibSerializerPhase
			// Compiling the CLR stdlib ITSELF (`-Xstdlib-compilation`): the fragment-actualized common+clr source
			// frontend (stdlib self-build needs real bodies the frontend klib does not carry). Checked AFTER the
			// klib branch — `build-stdlib-klib.sh` also passes `-Xstdlib-compilation`, so the klib build must win.
			arguments.stdlibCompilation ->
				ClrMetadataConfigurationPipelinePhase then
					ClrStdlibFrontendPipelinePhase then
					ClrCommonFir2IrPipelinePhase then
					ClrBackendPhase
			else ->
				ClrMetadataConfigurationPipelinePhase then
					ClrPluginRegistrationPhase then
					ClrAppFrontendPipelinePhase then
					ClrCommonFir2IrPipelinePhase then
					ClrBackendPhase
		}
}
