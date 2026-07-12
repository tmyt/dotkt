@file:Suppress("DEPRECATION")
@file:OptIn(org.jetbrains.kotlin.compiler.plugin.ExperimentalCompilerApi::class, org.jetbrains.kotlin.K1Deprecation::class)

package kotc.pipeline

import kotc.frontend.ClrCompilerPluginRegistrar
import org.jetbrains.kotlin.KtPsiSourceFile
import org.jetbrains.kotlin.cli.common.CLIConfigurationKeys
import org.jetbrains.kotlin.cli.common.checkKotlinPackageUsageForPsi
import org.jetbrains.kotlin.cli.common.diagnosticsCollector
import org.jetbrains.kotlin.cli.common.fileBelongsToModuleForPsi
import org.jetbrains.kotlin.cli.common.fir.FirDiagnosticsCompilerResultsReporter
import org.jetbrains.kotlin.cli.common.isCommonSourceForPsi
import org.jetbrains.kotlin.cli.common.messages.AnalyzerWithCompilerReport
import org.jetbrains.kotlin.cli.common.prepareNativeSessions
import org.jetbrains.kotlin.cli.jvm.compiler.EnvironmentConfigFiles
import org.jetbrains.kotlin.cli.jvm.compiler.KotlinCoreEnvironment
import org.jetbrains.kotlin.cli.jvm.config.K2MetadataConfigurationKeys
import org.jetbrains.kotlin.cli.jvm.config.jvmClasspathRoots
import org.jetbrains.kotlin.cli.jvm.config.jvmModularRoots
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.ConfigurationPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PerformanceNotifications
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
import org.jetbrains.kotlin.compiler.plugin.CompilerPluginRegistrar
import org.jetbrains.kotlin.compiler.plugin.getCompilerExtensions
import org.jetbrains.kotlin.config.CommonConfigurationKeys
import org.jetbrains.kotlin.config.messageCollector
import org.jetbrains.kotlin.config.moduleName
import org.jetbrains.kotlin.fir.DependencyListForCliModule
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.pipeline.AllModulesFrontendOutput
import org.jetbrains.kotlin.fir.pipeline.buildFirFromKtFiles
import org.jetbrains.kotlin.fir.pipeline.resolveAndCheckFir
import org.jetbrains.kotlin.fir.pipeline.runPlatformCheckers
import org.jetbrains.kotlin.name.Name
import java.io.File

object ClrStdlibFrontendPipelinePhase : PipelinePhase<ConfigurationPipelineArtifact, MetadataFrontendPipelineArtifact>(
	name = "ClrStdlibFrontendPipelinePhase",
	postActions = setOf(PerformanceNotifications.AnalysisFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: ConfigurationPipelineArtifact): MetadataFrontendPipelineArtifact {
		val (configuration, rootDisposable) = input
		val diagnosticsReporter = configuration.diagnosticsCollector
		configuration.add(CompilerPluginRegistrar.COMPILER_PLUGIN_REGISTRARS, ClrCompilerPluginRegistrar())
		val rootModuleName = Name.special("<${configuration.moduleName!!}>")
		val libraryList = DependencyListForCliModule.build(rootModuleName) {
			val refinedPaths = configuration.get(K2MetadataConfigurationKeys.REFINES_PATHS)?.map { File(it) }.orEmpty()
			dependencies(configuration.jvmClasspathRoots.filter { it !in refinedPaths }.map { it.absolutePath })
			dependencies(configuration.jvmModularRoots.map { it.absolutePath })
			friendDependencies(configuration[K2MetadataConfigurationKeys.FRIEND_PATHS] ?: emptyList())
			dependsOnDependencies(refinedPaths.map { it.absolutePath })
		}

		val environment = KotlinCoreEnvironment.createForProduction(
			rootDisposable,
			configuration,
			EnvironmentConfigFiles.METADATA_CONFIG_FILES,
		)
		val ktFiles = environment.getSourceFiles()
		for (ktFile in ktFiles) {
			AnalyzerWithCompilerReport.reportSyntaxErrors(ktFile, diagnosticsReporter)
		}
		val extensionRegistrars = configuration.getCompilerExtensions(FirExtensionRegistrar)
		val sessionsWithSources = prepareNativeSessions(
			ktFiles,
			configuration,
			rootModuleName,
			resolvedLibraries = emptyList(),
			libraryList,
			extensionRegistrars,
			metadataCompilationMode = false,
			isCommonSourceForPsi,
			fileBelongsToModuleForPsi,
		)
		val outputs = sessionsWithSources.map { (session, files) ->
			installKotlinJvmDefaultImport(session)
			resolveAndCheckFir(session, session.buildFirFromKtFiles(files), diagnosticsReporter)
		}
		outputs.runPlatformCheckers(diagnosticsReporter)
		checkKotlinPackageUsageForPsi(configuration, ktFiles)
		FirDiagnosticsCompilerResultsReporter.reportToMessageCollector(
			diagnosticsReporter,
			configuration.messageCollector,
			configuration.getBoolean(CLIConfigurationKeys.RENDER_DIAGNOSTIC_INTERNAL_NAME),
		)
		return MetadataFrontendPipelineArtifact(
			AllModulesFrontendOutput(outputs),
			configuration,
			ktFiles.map { KtPsiSourceFile(it) },
		)
	}
}
