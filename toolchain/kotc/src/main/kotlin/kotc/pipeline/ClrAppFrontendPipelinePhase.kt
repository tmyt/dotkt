@file:Suppress("DEPRECATION")

package kotc.pipeline

import org.jetbrains.kotlin.KtPsiSourceFile
import org.jetbrains.kotlin.com.intellij.openapi.vfs.StandardFileSystems
import org.jetbrains.kotlin.com.intellij.openapi.vfs.VirtualFileManager
import org.jetbrains.kotlin.cli.common.CLIConfigurationKeys
import org.jetbrains.kotlin.cli.common.checkKotlinPackageUsageForPsi
import org.jetbrains.kotlin.cli.common.fileBelongsToModuleForPsi
import org.jetbrains.kotlin.cli.common.fir.FirDiagnosticsCompilerResultsReporter
import org.jetbrains.kotlin.cli.common.isCommonSourceForPsi
import org.jetbrains.kotlin.cli.common.messages.AnalyzerWithCompilerReport
import org.jetbrains.kotlin.cli.common.messages.toLogger
import org.jetbrains.kotlin.cli.common.prepareMetadataSessions
import org.jetbrains.kotlin.cli.jvm.compiler.EnvironmentConfigFiles
import org.jetbrains.kotlin.cli.jvm.compiler.KotlinCoreEnvironment
import org.jetbrains.kotlin.cli.jvm.compiler.VfsBasedProjectEnvironment
import org.jetbrains.kotlin.cli.jvm.compiler.createContextForIncrementalCompilation
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
import org.jetbrains.kotlin.config.CommonConfigurationKeys
import org.jetbrains.kotlin.config.messageCollector
import org.jetbrains.kotlin.config.moduleName
import org.jetbrains.kotlin.fir.DependencyListForCliModule
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.pipeline.FirResult
import org.jetbrains.kotlin.fir.pipeline.buildFirFromKtFiles
import org.jetbrains.kotlin.fir.pipeline.resolveAndCheckFir
import org.jetbrains.kotlin.fir.pipeline.runPlatformCheckers
import org.jetbrains.kotlin.library.metadata.resolver.impl.KotlinResolvedLibraryImpl
import org.jetbrains.kotlin.library.resolveSingleFileKlib
import org.jetbrains.kotlin.name.Name
import java.io.File

/**
 * Fork of the stock (pinned 2.2.0) `MetadataFrontendPipelinePhase` -- PSI-only, non-light-tree
 * branch, matching the rest of kotc, which never sets [CommonConfigurationKeys.USE_LIGHT_TREE] --
 * needed for exactly one reason: the stock phase builds its FIR sessions internally via
 * `prepareMetadataSessions` and only returns the already-resolved [FirResult] -- it never hands back
 * the `FirSession` objects before resolution runs. We need that window to call
 * [installKotlinJvmDefaultImport] (see `ClrDefaultImports.kt` for why) before any file's imports get
 * resolved. `prepareMetadataSessions` itself IS public CLI API (`org.jetbrains.kotlin.cli.common`), so
 * this reimplements only the thin CLI glue around it (verbatim from `v2.2.0`, the pinned compiler
 * tag -- upstream's live `MetadataFrontendPipelinePhase.kt` has since diverged, e.g. to
 * `loadMetadataKlibs`, which doesn't exist in the 2.2.0 jar kotc actually embeds), not Kotlin-core
 * session-construction logic.
 */
object ClrAppFrontendPipelinePhase : PipelinePhase<ConfigurationPipelineArtifact, MetadataFrontendPipelineArtifact>(
	name = "ClrAppFrontendPipelinePhase",
	postActions = setOf(PerformanceNotifications.AnalysisFinished, CheckCompilationErrors.CheckDiagnosticCollector),
) {
	override fun executePhase(input: ConfigurationPipelineArtifact): MetadataFrontendPipelineArtifact {
		val (configuration, diagnosticsReporter, rootDisposable) = input
		val messageCollector = configuration.messageCollector
		val rootModuleName = Name.special("<${configuration.moduleName!!}>")

		val libraryList = DependencyListForCliModule.build(rootModuleName) {
			val refinedPaths = configuration.get(K2MetadataConfigurationKeys.REFINES_PATHS)?.map { File(it) }.orEmpty()
			dependencies(configuration.jvmClasspathRoots.filter { it !in refinedPaths }.map { it.absolutePath })
			dependencies(configuration.jvmModularRoots.map { it.absolutePath })
			friendDependencies(configuration[K2MetadataConfigurationKeys.FRIEND_PATHS] ?: emptyList())
			dependsOnDependencies(refinedPaths.map { it.absolutePath })
		}

		val klibFiles = configuration.get(CLIConfigurationKeys.CONTENT_ROOTS).orEmpty()
			.filterIsInstance<JvmClasspathRoot>()
			.filter { it.file.isDirectory || it.file.extension == "klib" }
			.map { it.file.absolutePath }

		val logger = messageCollector.toLogger()
		// Mirrors stock 2.2.0's own KT-63573 workaround (see upstream MetadataFrontendPipelinePhase.kt):
		// resolve each klib file directly rather than via CommonKLibResolver.resolve(...).
		val resolvedLibraries = klibFiles.map {
			KotlinResolvedLibraryImpl(
				resolveSingleFileKlib(org.jetbrains.kotlin.konan.file.File(it), logger),
			)
		}

		val environment = KotlinCoreEnvironment.createForProduction(
			rootDisposable,
			configuration,
			EnvironmentConfigFiles.METADATA_CONFIG_FILES,
		)
		val projectEnvironment = VfsBasedProjectEnvironment(
			environment.project,
			VirtualFileManager.getInstance().getFileSystem(StandardFileSystems.FILE_PROTOCOL),
		) { environment.createPackagePartProvider(it) }
		var librariesScope = projectEnvironment.getSearchScopeForProjectLibraries()
		val extensionRegistrars = FirExtensionRegistrar.Companion.getInstances(projectEnvironment.project)
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
			librariesScope, libraryList, resolvedLibraries, isCommonSourceForPsi, fileBelongsToModuleForPsi,
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
			FirResult(outputs),
			configuration,
			diagnosticsReporter,
			sourceFiles,
		)
	}
}
