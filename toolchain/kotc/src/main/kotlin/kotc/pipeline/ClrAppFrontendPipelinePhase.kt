@file:Suppress("DEPRECATION")
@file:OptIn(org.jetbrains.kotlin.K1Deprecation::class)

package kotc.pipeline

import org.jetbrains.kotlin.KtPsiSourceFile
import org.jetbrains.kotlin.backend.common.loadMetadataKlibs
import org.jetbrains.kotlin.cli.common.CLIConfigurationKeys
import org.jetbrains.kotlin.cli.common.checkKotlinPackageUsageForPsi
import org.jetbrains.kotlin.cli.common.diagnosticsCollector
import org.jetbrains.kotlin.cli.common.fileBelongsToModuleForPsi
import org.jetbrains.kotlin.cli.common.fir.FirDiagnosticsCompilerResultsReporter
import org.jetbrains.kotlin.cli.common.isCommonSourceForPsi
import org.jetbrains.kotlin.cli.common.messages.AnalyzerWithCompilerReport
import org.jetbrains.kotlin.cli.common.prepareMetadataSessions
import org.jetbrains.kotlin.cli.jvm.compiler.EnvironmentConfigFiles
import org.jetbrains.kotlin.cli.jvm.compiler.KotlinCoreEnvironment
import org.jetbrains.kotlin.cli.jvm.compiler.createContextForIncrementalCompilation
import org.jetbrains.kotlin.cli.jvm.compiler.toVfsBasedProjectEnvironment
import org.jetbrains.kotlin.cli.jvm.config.JvmClasspathRoot
import org.jetbrains.kotlin.cli.jvm.config.K2MetadataConfigurationKeys
import org.jetbrains.kotlin.cli.jvm.config.jvmClasspathRoots
import org.jetbrains.kotlin.cli.jvm.config.jvmModularRoots
import org.jetbrains.kotlin.cli.pipeline.CheckCompilationErrors
import org.jetbrains.kotlin.cli.pipeline.ConfigurationPipelineArtifact
import org.jetbrains.kotlin.cli.pipeline.PerformanceNotifications
import org.jetbrains.kotlin.cli.pipeline.PipelinePhase
import org.jetbrains.kotlin.cli.pipeline.jvm.asKtFilesList
import org.jetbrains.kotlin.cli.pipeline.metadata.MetadataFrontendPipelineArtifact
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
import org.jetbrains.kotlin.library.KotlinLibrary
import org.jetbrains.kotlin.name.Name
import java.io.File

/**
 * Fork of the stock `MetadataFrontendPipelinePhase` (2.4.0) -- PSI-only, non-light-tree branch,
 * matching the rest of kotc, which never sets [CommonConfigurationKeys.USE_LIGHT_TREE] -- needed for
 * exactly one reason: the stock phase builds its FIR sessions internally via `prepareMetadataSessions`
 * and only returns the already-resolved frontend output -- it never hands back the `FirSession` objects
 * before resolution runs. We need that window to call [installKotlinJvmDefaultImport] (see
 * `ClrDefaultImports.kt` for why) before any file's imports get resolved. `prepareMetadataSessions`
 * itself IS public CLI API (`org.jetbrains.kotlin.cli.common`), so this reimplements only the thin CLI
 * glue around it (kept in sync with the stock 2.4.0 non-light-tree branch: `loadMetadataKlibs`,
 * `toVfsBasedProjectEnvironment`, `getCompilerExtensions`), not Kotlin-core session-construction logic.
 */
object ClrAppFrontendPipelinePhase : PipelinePhase<ConfigurationPipelineArtifact, MetadataFrontendPipelineArtifact>(
	name = "ClrAppFrontendPipelinePhase",
	postActions = setOf(PerformanceNotifications.AnalysisFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: ConfigurationPipelineArtifact): MetadataFrontendPipelineArtifact {
		val (configuration, rootDisposable) = input
		val diagnosticsReporter = configuration.diagnosticsCollector
		val messageCollector = configuration.messageCollector
		val rootModuleName = Name.special("<${configuration.moduleName!!}>")

		val libraryList = DependencyListForCliModule.build(rootModuleName) {
			val refinedPaths = configuration.get(K2MetadataConfigurationKeys.REFINES_PATHS)?.map { File(it) }.orEmpty()
			dependencies(configuration.jvmClasspathRoots.filter { it !in refinedPaths }.map { it.absolutePath })
			dependencies(configuration.jvmModularRoots.map { it.absolutePath })
			friendDependencies(configuration[K2MetadataConfigurationKeys.FRIEND_PATHS] ?: emptyList())
			dependsOnDependencies(refinedPaths.map { it.absolutePath })
		}

		// 2.4.0's stock metadata frontend (MetadataFrontendPipelinePhase) does exactly this: load the
		// classpath klibs via the KlibLoader entry point rather than resolveSingleFileKlib. This subsumed
		// kotc's old KT-63573 manual-resolution workaround (its own comment predicted the migration).
		val klibs: List<KotlinLibrary> = loadMetadataKlibs(
			libraryPaths = configuration.get(CLIConfigurationKeys.CONTENT_ROOTS).orEmpty()
				.filterIsInstance<JvmClasspathRoot>()
				.map { it.file.path },
			configuration = configuration,
		).all

		val environment = KotlinCoreEnvironment.createForProduction(
			rootDisposable,
			configuration,
			EnvironmentConfigFiles.METADATA_CONFIG_FILES,
		)
		val projectEnvironment = environment.toVfsBasedProjectEnvironment()
		var librariesScope = projectEnvironment.getSearchScopeForProjectLibraries()
		val extensionRegistrars = configuration.getCompilerExtensions(FirExtensionRegistrar)
		val ktFiles = environment.getSourceFiles()
		val sourceFiles = ktFiles.map { KtPsiSourceFile(it) }

		for (ktFile in ktFiles) {
			AnalyzerWithCompilerReport.reportSyntaxErrors(ktFile, diagnosticsReporter)
		}

		val sourceScope =
			projectEnvironment.getSearchScopeByPsiFiles(ktFiles) + projectEnvironment.getSearchScopeForProjectJavaSources()
		val providerAndScopeForIncrementalCompilation = createContextForIncrementalCompilation(
			projectEnvironment,
			configuration,
			sourceScope,
		)
		providerAndScopeForIncrementalCompilation?.precompiledBinariesFileScope?.let {
			librariesScope -= it
		}

		val sessionsWithSources = prepareMetadataSessions(
			ktFiles, configuration, projectEnvironment, rootModuleName, extensionRegistrars,
			librariesScope, libraryList, klibs, isCommonSourceForPsi, fileBelongsToModuleForPsi,
			createProviderAndScopeForIncrementalCompilation = { providerAndScopeForIncrementalCompilation },
		)

		val outputs = sessionsWithSources.map { (session, files) ->
			installKotlinJvmDefaultImport(session)
			val firFiles = session.buildFirFromKtFiles(files)
			resolveAndCheckFir(session, firFiles, diagnosticsReporter)
		}

		outputs.runPlatformCheckers(diagnosticsReporter)
		checkKotlinPackageUsageForPsi(configuration, sourceFiles.asKtFilesList())

		val renderDiagnosticNames = configuration.getBoolean(CLIConfigurationKeys.RENDER_DIAGNOSTIC_INTERNAL_NAME)
		FirDiagnosticsCompilerResultsReporter.reportToMessageCollector(diagnosticsReporter, messageCollector, renderDiagnosticNames)
		return MetadataFrontendPipelineArtifact(
			AllModulesFrontendOutput(outputs),
			configuration,
			sourceFiles,
		)
	}
}
