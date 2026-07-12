package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.FirSessionComponent
import org.jetbrains.kotlin.fir.SessionConfiguration
import org.jetbrains.kotlin.fir.scopes.FirDefaultImportsProviderHolder
import org.jetbrains.kotlin.fir.scopes.defaultImportsProvider
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.resolve.DefaultImportsProvider
import org.jetbrains.kotlin.resolve.ImportPath

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
 * The only session-level extensibility point for default imports is the `FirDefaultImportsProviderHolder`
 * session component (a `DefaultImportsProvider` instance) registered once by the session factory in
 * `registerDefaultComponents()`/`registerNativeComponents()`; there is no `FirExtensionRegistrar` hook or
 * `FirSessionConfigurator` API to *append* an import ourselves. `FirSession.register` writes an
 * un-immutable array-map slot (`ComponentArrayOwner`/`ArrayMapImpl.set`), so re-registering the same key
 * with a wrapping provider is a safe overwrite -- PROVIDED it happens before any FIR resolution reads it.
 * Verified: the default-import scope (`FirDefaultSimpleImportingScope.simpleImports`, upstream
 * `compiler/fir/resolve/.../FirDefaultSimpleImportingScope.kt`) reads `session.defaultImportsProvider`
 * lazily inside a per-file scope built during import/body resolution -- never at session-construction
 * time -- so calling [installKotlinJvmDefaultImport] right after the session is built (before
 * `resolveAndCheckFir`) is both safe and effective.
 *
 * 2.4.0 GOTCHA: `FirDefaultImportsProviderHolder` became a `FirComposableSessionComponent`, and
 * `FirSession` gained a generic `register(KClass<out T>, value: T)` overload (for `T :
 * FirComposableSessionComponent<T>`) that COMPOSES with the existing holder instead of overwriting it.
 * Worse, composition INTERSECTS platform imports (`DefaultImportsProvider.Composed
 * .platformSpecificDefaultImports` = `reduce { acc, list -> acc.intersect(list) }`), so composing our
 * `delegate.psdi + kotlin.jvm.*` back with the original `delegate.psdi` yields just `delegate.psdi` --
 * our addition is silently erased and the whole call becomes a no-op. So we UPCAST the value to
 * `FirSessionComponent` to force the plain `register(KClass<out FirSessionComponent>, FirSessionComponent)`
 * overload, which does the array-map OVERWRITE we need.
 */
@OptIn(SessionConfiguration::class)
fun installKotlinJvmDefaultImport(session: FirSession) {
	val delegate = session.defaultImportsProvider
	session.register(
		FirDefaultImportsProviderHolder::class,
		FirDefaultImportsProviderHolder.of(JvmAwareDefaultImportsProvider(delegate)) as FirSessionComponent,
	)
}

private class JvmAwareDefaultImportsProvider(private val delegate: DefaultImportsProvider) : DefaultImportsProvider() {
	override val platformSpecificDefaultImports: List<ImportPath>
		get() = delegate.platformSpecificDefaultImports + ImportPath.fromString("kotlin.jvm.*")

	override val defaultLowPriorityImports: List<ImportPath> get() = delegate.defaultLowPriorityImports
	override val excludedImports: List<FqName> get() = delegate.excludedImports
}
