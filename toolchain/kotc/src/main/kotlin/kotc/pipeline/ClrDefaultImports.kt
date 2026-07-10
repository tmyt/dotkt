package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.SessionConfiguration
import org.jetbrains.kotlin.fir.scopes.FirDefaultImportProviderHolder
import org.jetbrains.kotlin.fir.scopes.defaultImportProvider
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.resolve.DefaultImportProvider
import org.jetbrains.kotlin.resolve.ImportPath
import org.jetbrains.kotlin.storage.StorageManager

/**
 * kotc runs the stock FIR frontend against the Common/Native-platform session factories
 * (`FirMetadataSessionFactory` for the app build via [ClrAppFrontendPipelinePhase], a fork of the
 * stock `MetadataFrontendPipelinePhase`; `FirNativeSessionFactory` for the stdlib klib/ref/rt builds
 * via [ClrStdlibFrontendPipelinePhase] / `ClrMetadataKlibPipeline`) rather than the JVM session
 * factory. Neither Common's
 * `CommonPlatformAnalyzerServices` nor Native's `NativePlatformAnalyzerServices`
 * `computePlatformSpecificDefaultImports` adds `kotlin.jvm.*` — that's JVM-only
 * (`FirJvmDefaultImportProvider`, see upstream `compiler/fir/fir-jvm/.../FirJvmDefaultImportProvider.kt`).
 * `@JvmInline`/`@JvmStatic`/`@JvmOverloads`/... therefore don't resolve as a default import on kotc's
 * non-JVM sessions, even though the stdlib and user sources use them unqualified (matching upstream
 * JVM/Native behavior for those annotations, which DO get `kotlin.jvm.*` as a default import on their
 * respective platforms).
 *
 * This is pure Kotlin-frontend session setup — no CLR/BCL knowledge — so it belongs in kotc, not
 * bir2cir: it decides what *Kotlin* names are visible unqualified, not how a Kotlin construct maps to
 * the CLR.
 *
 * The only session-level extensibility point for default imports is the `FirDefaultImportProviderHolder`
 * session component (a `DefaultImportProvider` instance) registered once by the session factory in
 * `registerDefaultComponents()`/`registerNativeComponents()`; there is no `FirExtensionRegistrar` hook or
 * `FirSessionConfigurator` API to *append* an import ourselves. `FirSession.register` is a plain,
 * un-immutable array-map slot (`ComponentArrayOwner`/`ArrayMapImpl.set`), so re-registering the same key
 * with a wrapping provider is a safe overwrite -- PROVIDED it happens before any FIR resolution reads it.
 * Verified: the default-import scope (`FirDefaultSimpleImportingScope.simpleImports`, upstream
 * `compiler/fir/resolve/.../FirDefaultSimpleImportingScope.kt`) reads `session.defaultImportProvider`
 * lazily inside a per-file scope built during import/body resolution -- never at session-construction
 * time -- so calling [installKotlinJvmDefaultImport] right after the session is built (before
 * `resolveAndCheckFir`) is both safe and effective.
 */
@OptIn(SessionConfiguration::class)
fun installKotlinJvmDefaultImport(session: FirSession) {
	val delegate = session.defaultImportProvider
	session.register(FirDefaultImportProviderHolder::class, FirDefaultImportProviderHolder(JvmAwareDefaultImportProvider(delegate)))
}

private class JvmAwareDefaultImportProvider(private val delegate: DefaultImportProvider) : DefaultImportProvider() {
	override fun computePlatformSpecificDefaultImports(storageManager: StorageManager, result: MutableList<ImportPath>) {
		delegate.computePlatformSpecificDefaultImports(storageManager, result)
		result.add(ImportPath.fromString("kotlin.jvm.*"))
	}

	override val defaultLowPriorityImports: List<ImportPath> get() = delegate.defaultLowPriorityImports
	override val excludedImports: List<FqName> get() = delegate.excludedImports
}
