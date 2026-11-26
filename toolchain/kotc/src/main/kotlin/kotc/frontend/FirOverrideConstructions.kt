@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class,
    org.jetbrains.kotlin.ir.symbols.UnsafeDuringIrConstructionAPI::class)

package kotc.frontend

import org.jetbrains.kotlin.fir.backend.Fir2IrComponents
import org.jetbrains.kotlin.fir.backend.FirMetadataSource
import org.jetbrains.kotlin.fir.declarations.*
import org.jetbrains.kotlin.fir.resolve.lookupSuperTypes
import org.jetbrains.kotlin.fir.scopes.*
import org.jetbrains.kotlin.fir.scopes.impl.FirClassSubstitutionScope
import org.jetbrains.kotlin.fir.symbols.impl.FirNamedFunctionSymbol
import org.jetbrains.kotlin.fir.types.*
import org.jetbrains.kotlin.fir.unwrapSubstitutionOverrides
import org.jetbrains.kotlin.ir.declarations.IrClass
import org.jetbrains.kotlin.ir.declarations.IrSimpleFunction
import org.jetbrains.kotlin.ir.types.IrType

/** A CLR superinterface can inherit multiple constructions of a Kotlin generic declaration. FIR
 * selects the correct override in its substituted scope, whereas FIR2IR merges the fake symbols. */
internal class FirOverrideConstructions(private val components: Fir2IrComponents) {
    data class Entry(val declaration: IrSimpleFunction, val owner: IrType)
    private val repeated = mutableMapOf<FirClass, Set<ConeClassLikeType>>()
    private val selections = mutableMapOf<IrSimpleFunction, List<Entry>>()

    fun forMethod(fn: IrSimpleFunction): List<Entry> = selections.getOrPut(fn) { select(fn) }

    private fun select(fn: IrSimpleFunction): List<Entry> {
        val source = (fn.metadata as? FirMetadataSource.Function)?.fir as? FirNamedFunction ?: return emptyList()
        if (!source.status.isOverride) return emptyList()
        val owner = ((fn.parent as? IrClass)?.metadata as? FirMetadataSource.Class)?.fir ?: return emptyList()
        val session = owner.moduleData.session
        val constructions = repeated.getOrPut(owner) {
            lookupSuperTypes(owner, lookupInterfaces = true, deep = true, useSiteSession = session, substituteTypes = true)
                .filter { it.typeArguments.isNotEmpty() }.distinct().groupBy { it.lookupTag }
                .values.filter { it.size > 1 }.flatten().toSet()
        }
        if (constructions.isEmpty()) return emptyList()
        val scope = owner.unsubstitutedScope(session, components.scopeSession, false, FirResolvePhase.BODY_RESOLVE)
        scope.processFunctionsByName(fn.name) { }
        val seen = mutableSetOf<Pair<FirNamedFunctionSymbol, FirTypeScope>>()
        val selected = mutableSetOf<Pair<FirNamedFunctionSymbol, ConeKotlinType>>()
        fun visit(symbol: FirNamedFunctionSymbol, at: FirTypeScope) {
            if (!seen.add(symbol to at)) return
            if (at is FirClassSubstitutionScope) {
                val original = symbol.unwrapSubstitutionOverrides()
                val applied = original.fir.dispatchReceiverType?.let { at.substitutor.substituteOrSelf(it) }
                if (applied != null && applied in constructions) selected += original to applied
            }
            at.processDirectOverriddenFunctionsWithBaseScope(symbol) { next, nextScope ->
                visit(next, nextScope); ProcessorAction.NEXT
            }
        }
        visit(source.symbol, scope)
        return selected.map { (original, application) ->
            Entry(components.declarationStorage.getIrFunctionSymbol(original).owner as IrSimpleFunction,
                with(components.typeConverter) { application.toIrType() })
        }
    }
}
