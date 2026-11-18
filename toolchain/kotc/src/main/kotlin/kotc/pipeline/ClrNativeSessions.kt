/*
 * CLI glue adapted from Kotlin 2.4.21's FirSessionConstructionUtils.
 * Copyright 2010-2023 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Apache-2.0: https://github.com/JetBrains/kotlin/blob/v2.4.21/license/LICENSE.txt
 */
package kotc.pipeline

import org.jetbrains.kotlin.cli.common.SessionConstructionUtils
import org.jetbrains.kotlin.config.CompilerConfiguration
import org.jetbrains.kotlin.config.targetPlatform
import org.jetbrains.kotlin.fir.DependencyListForCliModule
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.deserialization.ModuleDataProvider
import org.jetbrains.kotlin.fir.extensions.FirExtensionRegistrar
import org.jetbrains.kotlin.fir.scopes.FirKotlinScopeProvider
import org.jetbrains.kotlin.fir.session.AbstractFirMetadataSessionFactory
import org.jetbrains.kotlin.fir.session.FirJsSessionFactory
import org.jetbrains.kotlin.fir.session.FirNativeSessionFactory
import org.jetbrains.kotlin.library.KotlinLibrary
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.platform.konan.NativePlatforms
import org.jetbrains.kotlin.psi.KtFile
import org.jetbrains.kotlin.utils.addToStdlib.shouldNotBeCalled

/** Kotlin 2.4.21's prepareNativeSessions/prepareKlibSessions CLI glue, retaining
 * metadataCompilationMode=false and its native factory/source-session behavior.
 * Only the manifest-selected metadata provider is substituted. */
internal fun prepareClrNativeSessions(
    files: List<KtFile>,
    configuration: CompilerConfiguration,
    rootModuleName: Name,
    resolvedLibraries: List<KotlinLibrary>,
    libraryList: DependencyListForCliModule,
    extensionRegistrars: List<FirExtensionRegistrar>,
    isCommonSource: (KtFile) -> Boolean,
    fileBelongsToModule: (KtFile, String) -> Boolean,
) = ClrKlibStringTables(resolvedLibraries).let { tables ->
    val fragmentTables = ClrFragmentStringTables()
    val factory = object : FirNativeSessionFactory() {
        override fun createAdditionalDependencyProviders(
            session: FirSession, moduleDataProvider: ModuleDataProvider,
            kotlinScopeProvider: FirKotlinScopeProvider, resolvedLibraries: List<KotlinLibrary>,
        ) = tables.providers(session, moduleDataProvider, kotlinScopeProvider) +
            super.createAdditionalDependencyProviders(session, moduleDataProvider, kotlinScopeProvider, tables.libraries)
    }
    // For a separate fragment the CLI reads the target from the configuration, not the
    // prepareSessions argument. Use the same native frontend for its metadata dependencies.
    val platform = NativePlatforms.unspecifiedNativePlatform
    SessionConstructionUtils.prepareSessions(
        files, configuration.copy().apply { targetPlatform = platform }, rootModuleName, platform,
        metadataCompilationMode = false, libraryList, extensionRegistrars,
        isCommonSource, isScript = { false }, fileBelongsToModule,
        createMetadataSessionFactoryContextForHmppCommonLibrarySession = {
            AbstractFirMetadataSessionFactory.Context(
                createJvmContext = { shouldNotBeCalled() },
                createJsContext = { FirJsSessionFactory.Context(configuration) },
            )
        },
        createSharedLibrarySession = { factory.createSharedLibrarySession(rootModuleName, configuration, extensionRegistrars) },
        createLibrarySession = { shared -> factory.createLibrarySession(
            tables.standard, shared, libraryList.moduleDataProvider, extensionRegistrars, configuration) },
        createSourceSession = { moduleData, kind, configure ->
            fragmentTables.beforeSourceSessions()
            factory.createSourceSession(moduleData, extensionRegistrars, configuration, kind, icData = null, init = configure)
        },
        additionalProvidersForMetadataLibrarySessionsInHmppMode = { session, modules, scopes, libraries ->
            fragmentTables.record(session, modules, scopes, libraries)
            FirNativeSessionFactory.createAdditionalDependencyProviders(session, modules, scopes, libraries)
        },
    )
}
