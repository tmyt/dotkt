@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class)

package kotc.pipeline

import org.jetbrains.kotlin.descriptors.ClassKind
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.declarations.FirClass
import org.jetbrains.kotlin.fir.declarations.getStringArgument
import org.jetbrains.kotlin.fir.expressions.FirAnnotation
import org.jetbrains.kotlin.fir.resolve.ScopeSession
import org.jetbrains.kotlin.fir.resolve.lookupSuperTypes
import org.jetbrains.kotlin.fir.resolve.toRegularClassSymbol
import org.jetbrains.kotlin.fir.resolve.substitution.ConeSubstitutor
import org.jetbrains.kotlin.fir.scopes.*
import org.jetbrains.kotlin.fir.symbols.impl.*
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name

private val externalClassId = ClassId.topLevel(FqName("kotlin.clr.ClrExternal"))
private val sourceViewName = Name.identifier("sourceView")

internal fun sourceInterfaceView(annotations: List<FirAnnotation>): String? = annotations
    .firstOrNull { (it.annotationTypeRef.coneType as? ConeClassLikeType)?.lookupTag?.classId == externalClassId }
    ?.getStringArgument(sourceViewName)?.takeIf { it.isNotEmpty() }

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
