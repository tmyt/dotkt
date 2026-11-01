@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.config.AnalysisFlags
import org.jetbrains.kotlin.diagnostics.impl.BaseDiagnosticsCollector
import org.jetbrains.kotlin.fir.languageVersionSettings
import org.jetbrains.kotlin.fir.analysis.checkers.MppCheckerKind
import org.jetbrains.kotlin.fir.declarations.FirClass
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirResolvePhase
import org.jetbrains.kotlin.fir.declarations.getStringArgument
import org.jetbrains.kotlin.fir.expressions.FirAnnotation
import org.jetbrains.kotlin.fir.pipeline.SingleModuleFrontendOutput
import org.jetbrains.kotlin.fir.pipeline.runCheckers
import org.jetbrains.kotlin.fir.pipeline.runResolution
import org.jetbrains.kotlin.fir.resolve.ScopeSession
import org.jetbrains.kotlin.fir.resolve.lookupSuperTypes
import org.jetbrains.kotlin.fir.resolve.toRegularClassSymbol
import org.jetbrains.kotlin.fir.resolve.substitution.ConeSubstitutor
import org.jetbrains.kotlin.fir.scopes.*
import org.jetbrains.kotlin.fir.symbols.impl.*
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name

private val externalClassId = ClassId.topLevel(FqName("kotlin.clr.ClrExternal"))
private val sourceViewName = Name.identifier("sourceView")
private val sourceViewLookup = ThreadLocal.withInitial { false }

/** A lookup view is not a declaration contract. Restore the mode even for failed or nested compilations. */
internal fun <T> withSourceInterfaceViewLookup(enabled: Boolean, action: () -> T): T {
    val previous = sourceViewLookup.get()
    sourceViewLookup.set(enabled)
    return try { action() } finally { sourceViewLookup.set(previous) }
}

/** The stock resolve/check pipeline, with protocol vocabulary available only during expression resolution. */
internal fun resolveAndCheckClrFir(session: FirSession, files: List<FirFile>, reporter: BaseDiagnosticsCollector): SingleModuleFrontendOutput {
    val (scopeSession, resolved) = withSourceInterfaceViewLookup(true) { session.runResolution(files) }
    if (!session.languageVersionSettings.getFlag(AnalysisFlags.headerMode))
        session.runCheckers(scopeSession, resolved, reporter, MppCheckerKind.Common)
    return SingleModuleFrontendOutput(session, scopeSession, resolved)
}

internal fun sourceInterfaceView(annotations: List<FirAnnotation>): String? = annotations
    .firstOrNull { (it.annotationTypeRef.coneType as? ConeClassLikeType)?.lookupTag?.classId == externalClassId }
    ?.getStringArgument(sourceViewName)?.takeIf { it.isNotEmpty() }

internal fun sourceInterfaceLookupScope(klass: FirClass, session: FirSession, scopeSession: ScopeSession,
    nativeScope: FirTypeScope, phase: FirResolvePhase?): FirTypeScope {
    if (!sourceViewLookup.get()) return nativeScope
    val classes = listOf(klass) + lookupSuperTypes(klass, lookupInterfaces = true, deep = true,
        useSiteSession = session, substituteTypes = false).mapNotNull { it.lookupTag.toRegularClassSymbol(session)?.fir }
    val views = classes.mapNotNull { sourceInterfaceView(it.annotations) }.distinct().map { name ->
        val symbol = session.symbolProvider.getClassLikeSymbolByClassId(ClassId.topLevel(FqName(name))) as FirRegularClassSymbol
        withSourceInterfaceViewLookup(false) { symbol.fir.unsubstitutedScope(session, scopeSession, false, phase) }
    }
    return if (views.isEmpty()) nativeScope else SourceViewLookupScope(nativeScope, views)
}

/** Native members keep their vocabulary; a protocol supplies lookup aliases, not inherited declarations. */
private class SourceViewLookupScope(private val native: FirTypeScope, private val views: List<FirTypeScope>) : FirTypeScope() {
    override fun getCallableNames() = (listOf(native) + views).flatMapTo(linkedSetOf()) { it.getCallableNames() }
    override fun getClassifierNames() = native.getClassifierNames()
    override fun mayContainName(name: Name) = native.mayContainName(name) || views.any { it.mayContainName(name) }
    override val scopeOwnerLookupNames get() = native.scopeOwnerLookupNames
    override fun processFunctionsByName(name: Name, processor: (FirNamedFunctionSymbol) -> Unit) {
        var found = false
        native.processFunctionsByName(name) { found = true; processor(it) }
        if (!found) views.forEach { it.processFunctionsByName(name, processor) }
    }
    override fun processPropertiesByName(name: Name, processor: (FirVariableSymbol<*>) -> Unit) {
        var found = false
        native.processPropertiesByName(name) { found = true; processor(it) }
        if (!found) views.forEach { it.processPropertiesByName(name, processor) }
    }
    override fun processClassifiersByNameWithSubstitution(name: Name, processor: (FirClassifierSymbol<*>, ConeSubstitutor) -> Unit) =
        native.processClassifiersByNameWithSubstitution(name, processor)
    override fun processDeclaredConstructors(processor: (FirConstructorSymbol) -> Unit) = native.processDeclaredConstructors(processor)
    override fun processDirectOverriddenFunctionsWithBaseScope(functionSymbol: FirNamedFunctionSymbol,
        processor: (FirNamedFunctionSymbol, FirTypeScope) -> ProcessorAction) =
        native.processDirectOverriddenFunctionsWithBaseScope(functionSymbol, processor)
    override fun processDirectOverriddenPropertiesWithBaseScope(propertySymbol: FirPropertySymbol,
        processor: (FirPropertySymbol, FirTypeScope) -> ProcessorAction) =
        native.processDirectOverriddenPropertiesWithBaseScope(propertySymbol, processor)
    @DelicateScopeAPI
    override fun withReplacedSessionOrNull(newSession: FirSession, newScopeSession: ScopeSession): FirTypeScope? {
        val replacedNative = native.withReplacedSessionOrNull(newSession, newScopeSession) ?: native
        val replacedViews = views.map { it.withReplacedSessionOrNull(newSession, newScopeSession) ?: it }
        return SourceViewLookupScope(replacedNative, replacedViews)
    }
}

/** A projected protocol view supplies assignability, not a second set of native implementation obligations. */
internal fun sourceInterfaceViewScope(klass: FirClass, session: FirSession, scope: FirTypeScope,
    sourceView: String?): FirTypeScope {
    if (sourceView == null) return scope
    val view = klass.superTypeRefs.asSequence().mapNotNull { it.coneType as? ConeClassLikeType }
        .firstOrNull { it.lookupTag.classId.asSingleFqName().asString() == sourceView }
        ?.lookupTag?.toRegularClassSymbol(session) ?: return scope
    val owners = lookupSuperTypes(view.fir, lookupInterfaces = true, deep = true,
        useSiteSession = session, substituteTypes = false)
        .mapNotNull { it.lookupTag.toRegularClassSymbol(session) }
        .filter { it.fir.classKind == ClassKind.INTERFACE }.mapTo(mutableSetOf()) { it.classId }
    owners += view.classId
    return SourceViewScope(scope, owners)
}

private class SourceViewScope(private val delegate: FirTypeScope, private val viewOwners: Set<ClassId>) : FirTypeScope() {
    private fun visible(symbol: FirCallableSymbol<*>) = symbol.callableId?.classId !in viewOwners
    override fun getCallableNames() = delegate.getCallableNames()
    override fun getClassifierNames() = delegate.getClassifierNames()
    override fun mayContainName(name: Name) = delegate.mayContainName(name)
    override val scopeOwnerLookupNames get() = delegate.scopeOwnerLookupNames
    override fun processFunctionsByName(name: Name, processor: (FirNamedFunctionSymbol) -> Unit) =
        delegate.processFunctionsByName(name) { if (visible(it)) processor(it) }
    override fun processPropertiesByName(name: Name, processor: (FirVariableSymbol<*>) -> Unit) =
        delegate.processPropertiesByName(name) { if (visible(it)) processor(it) }
    override fun processClassifiersByNameWithSubstitution(name: Name, processor: (FirClassifierSymbol<*>, ConeSubstitutor) -> Unit) =
        delegate.processClassifiersByNameWithSubstitution(name, processor)
    override fun processDeclaredConstructors(processor: (FirConstructorSymbol) -> Unit) =
        delegate.processDeclaredConstructors(processor)
    override fun processDirectOverriddenFunctionsWithBaseScope(functionSymbol: FirNamedFunctionSymbol,
        processor: (FirNamedFunctionSymbol, FirTypeScope) -> ProcessorAction) =
        delegate.processDirectOverriddenFunctionsWithBaseScope(functionSymbol) { symbol, scope ->
            if (visible(symbol)) processor(symbol, scope) else ProcessorAction.NEXT
        }
    override fun processDirectOverriddenPropertiesWithBaseScope(propertySymbol: FirPropertySymbol,
        processor: (FirPropertySymbol, FirTypeScope) -> ProcessorAction) =
        delegate.processDirectOverriddenPropertiesWithBaseScope(propertySymbol) { symbol, scope ->
            if (visible(symbol)) processor(symbol, scope) else ProcessorAction.NEXT
        }
    @DelicateScopeAPI
    override fun withReplacedSessionOrNull(newSession: FirSession, newScopeSession: ScopeSession) =
        delegate.withReplacedSessionOrNull(newSession, newScopeSession)?.let { SourceViewScope(it, viewOwners) }
}
