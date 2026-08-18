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
import org.jetbrains.kotlin.cli.common.SessionConstructionUtils
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
import org.jetbrains.kotlin.config.languageVersionSettings
import org.jetbrains.kotlin.config.messageCollector
import org.jetbrains.kotlin.config.moduleName
import org.jetbrains.kotlin.config.targetPlatform
import org.jetbrains.kotlin.fir.DependencyListForCliModule
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.pipeline.AllModulesFrontendOutput
import org.jetbrains.kotlin.fir.pipeline.buildFirFromKtFiles
import org.jetbrains.kotlin.fir.pipeline.resolveAndCheckFir
import org.jetbrains.kotlin.fir.pipeline.runPlatformCheckers
import org.jetbrains.kotlin.fir.session.AbstractFirMetadataSessionFactory
import org.jetbrains.kotlin.fir.session.FirJsSessionFactory
import org.jetbrains.kotlin.fir.session.FirJvmSessionFactory
import org.jetbrains.kotlin.fir.session.FirMetadataSessionFactory
import org.jetbrains.kotlin.library.KotlinLibrary
import org.jetbrains.kotlin.load.kotlin.PackageAndMetadataPartProvider
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.platform.CommonPlatforms
import java.io.File

/**
 * Fork of the stock `MetadataFrontendPipelinePhase` (2.4.0) -- PSI-only, non-light-tree branch,
 * matching the rest of kotc, which never sets [CommonConfigurationKeys.USE_LIGHT_TREE] -- needed for
 * TWO reasons:
 *  1. The stock phase builds its FIR sessions internally via `prepareMetadataSessions` and only returns
 *     the already-resolved frontend output -- it never hands back the `FirSession` objects before
 *     resolution runs. We need that window to call [installKotlinJvmDefaultImport] (see
 *     `ClrDefaultImports.kt` for why) before any file's imports get resolved.
 *  2. `prepareMetadataSessions` hardcodes `metadataCompilationMode = true`, which collapses common +
 *     platform sources into a SINGLE FIR module -- fatal for a user MPP app (`expect`/`actual` in the
 *     same module). We inline its body (all public CLI/FIR symbols: `FirMetadataSessionFactory`,
 *     `SessionConstructionUtils.prepareSessions`, ...) to drive that flag off `hasCommonSources` and get
 *     the common/platform module split. See the call site below.
 *
 * `prepareMetadataSessions` itself IS public CLI API (`org.jetbrains.kotlin.cli.common`), so this
 * reimplements only the thin CLI glue around it plus its (now flag-parameterised) session-preparation
 * body (kept in sync with the stock 2.4.0 non-light-tree branch: `loadMetadataKlibs`,
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

		// This inlines the body of the stock `prepareMetadataSessions` (all public symbols) for ONE reason:
		// that function hardcodes `metadataCompilationMode = true`, which forces
		// `SessionConstructionUtils.prepareSessions` down the `createSingleSession` branch — common+platform
		// sources collapse into ONE `FirModuleData`, so an `expect` and its `actual` land in the same module
		// ("expect and corresponding actual are declared in the same module"). For a user MPP app the common
		// sources (marked via `-Xcommon-sources`) must form a separate common module that the platform module
		// refines, so expect/actual matches ACROSS the boundary. We therefore drive `metadataCompilationMode`
		// off whether any common source is present: with NO common sources the flag stays `true` and the path
		// is byte-identical to the stock single-session app compile (the non-MPP `cases/il-*` samples); with
		// common sources present it flips to `false`, and since `-Xmulti-platform` is always on (Main.kt) and
		// no `-Xfragments`/HMPP structure is passed, `prepareSessions` takes the legacy-MPP split
		// (common module + platform-module-depends-on-common). Downstream Fir2Ir actualization + BIR emit
		// already handle the multi-session output — the stdlib self-build uses the same two tail phases.
		// The block below is a verbatim copy of `prepareMetadataSessions` (upstream
		// `compiler/cli/.../common/FirSessionConstructionUtils.kt`, 2.4.0) with the sole `metadataCompilationMode`
		// literal parameterised — keep it in sync on a frontend bump. NOTE: `-Xfragments` app builds (an HMPP
		// module structure rather than `-Xcommon-sources`) are NOT wired here yet — `hasCommonSources` would be
		// false and force single-session; widening the guard to also cover an HMPP structure is a follow-up.
		val hasCommonSources = ktFiles.any { isCommonSourceForPsi(it) }
		val packagePartProvider =
			projectEnvironment.getPackagePartProvider(librariesScope) as PackageAndMetadataPartProvider
		val languageVersionSettings = configuration.languageVersionSettings
		val targetPlatform = configuration.targetPlatform ?: CommonPlatforms.defaultCommonPlatform
		val sessionFactory = FirMetadataSessionFactory(targetPlatform)
		val metadataContext = AbstractFirMetadataSessionFactory.Context(
			createJvmContext = {
				FirJvmSessionFactory.Context(
					configuration,
					projectEnvironment,
					librariesScope,
					registerJvmDeserializationExtension = false,
				)
			},
			createJsContext = { FirJsSessionFactory.Context(configuration) },
		)
		val sessionsWithSources = SessionConstructionUtils.prepareSessions(
			ktFiles, configuration, rootModuleName, targetPlatform,
			metadataCompilationMode = !hasCommonSources, libraryList, extensionRegistrars,
			isCommonSourceForPsi, isScript = { false }, fileBelongsToModuleForPsi,
			createMetadataSessionFactoryContextForHmppCommonLibrarySession = { metadataContext },
			createSharedLibrarySession = {
				sessionFactory.createSharedLibrarySession(
					rootModuleName, languageVersionSettings, extensionRegistrars, metadataContext,
				)
			},
			createLibrarySession = { sharedLibrarySession ->
				sessionFactory.createLibrarySession(
					sharedLibrarySession,
					libraryList.moduleDataProvider,
					extensionRegistrars,
					AbstractFirMetadataSessionFactory.JarMetadataProviderComponents(
						packagePartProvider, librariesScope, projectEnvironment,
					),
					klibs,
					languageVersionSettings,
					metadataContext,
				)
			},
			createSourceSession = { _, moduleData, isForLeafHmppModule, sessionConfigurator ->
				sessionFactory.createSourceSession(
					moduleData,
					projectEnvironment,
					incrementalCompilationContext = providerAndScopeForIncrementalCompilation,
					extensionRegistrars,
					configuration,
					metadataContext,
					isForLeafHmppModule,
					init = sessionConfigurator,
				)
			},
		)

		// One pipeline execution = one set of frontend-only facts. Both tables are objects, so their maps would
		// otherwise outlive the compilation inside a HOSTED kotc and a later run could read a stale entry.
		kotc.frontend.ClrContextFnTypes.reset()
		kotc.frontend.ClrCompanionExtensions.reset()
		kotc.frontend.ClrProjectedMemberExtensionProperties.reset()
		val outputs = sessionsWithSources.map { (session, files) ->
			installKotlinJvmDefaultImport(session)
			val firFiles = session.buildFirFromKtFiles(files)
			resolveAndCheckFir(session, firFiles, diagnosticsReporter).also {
				// Capture the CONTEXT-FUNCTION-TYPE arities while FIR still has them — fir2ir erases the
				// `ContextFunctionTypeParams` cone attribute, and `context(A) B.(D) -> E` becomes indistinguishable
				// from `B.(A, D) -> E` at IR level. See [kotc.frontend.ClrContextFnTypes].
				kotc.frontend.ClrContextFnTypes.capture(it.fir)
				// Capture the COMPANION-EXTENSION receiver types for the same reason: fir2ir drops a
				// `companion fun C.foo()`'s receiver parameter outright. See [kotc.frontend.ClrCompanionExtensions].
				kotc.frontend.ClrCompanionExtensions.capture(session, it.fir)
				// A projected method-generic member-extension property may become a raw accessor in fir2ir. Preserve
				// the FIR-resolved property/accessor identity for that exact use before the association is erased.
				kotc.frontend.ClrProjectedMemberExtensionProperties.capture(session, it.fir)
			}
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
