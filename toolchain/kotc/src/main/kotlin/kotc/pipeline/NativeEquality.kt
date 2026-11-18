@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class, org.jetbrains.kotlin.resolve.calls.tower.ApplicabilityDetail::class)

package kotc.pipeline

import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.declarations.*
import org.jetbrains.kotlin.fir.declarations.utils.isStatic
import org.jetbrains.kotlin.fir.expressions.*
import org.jetbrains.kotlin.fir.expressions.builder.*
import org.jetbrains.kotlin.fir.references.FirReference
import org.jetbrains.kotlin.fir.references.builder.buildSimpleNamedReference
import org.jetbrains.kotlin.fir.resolve.*
import org.jetbrains.kotlin.fir.resolve.calls.candidate.FirNamedReferenceWithCandidate
import org.jetbrains.kotlin.fir.resolve.calls.candidate.FirErrorReferenceWithCandidate
import org.jetbrains.kotlin.fir.resolve.diagnostics.ConeAmbiguityError
import org.jetbrains.kotlin.fir.resolve.transformers.body.resolve.FirAbstractBodyResolveTransformer
import org.jetbrains.kotlin.fir.scopes.impl.FirLocalScope
import org.jetbrains.kotlin.fir.scopes.impl.declaredMemberScope
import org.jetbrains.kotlin.fir.symbols.impl.FirRegularClassSymbol
import org.jetbrains.kotlin.fir.types.*
import org.jetbrains.kotlin.fir.visitors.FirTransformer
import org.jetbrains.kotlin.fir.visitors.FirVisitor
import org.jetbrains.kotlin.KtRealSourceElementKind
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.resolve.calls.tower.isSuccess

/** Select while the surrounding body is being resolved: a native predicate's true result
 * proves nothing about the operand types. Replacing equality after DFA would leave invalid smart casts. */
internal class NativeEquality {
    private val external = ClassId.topLevel(FqName("kotlin.clr.ClrExternal"))
    private val nativeMember = Name.identifier("memberName")
    private val cache = mutableMapOf<Pair<FirRegularClassSymbol, Name>, List<FirNamedFunction>>()
    private fun candidates(session: FirSession, owner: FirRegularClassSymbol, name: Name): List<FirNamedFunction> = cache.getOrPut(owner to name) {
        val types = listOf(owner) + lookupSuperTypes(owner.fir, lookupInterfaces = false, deep = true,
            useSiteSession = session, substituteTypes = false).mapNotNull { it.lookupTag.toRegularClassSymbol(session) }
        buildList {
            for (type in types.filter { it.hasAnnotation(external, session) }) session.declaredMemberScope(type, FirResolvePhase.STATUS).processFunctionsByName(name) {
                val fn = it.fir
                if (fn.isStatic && fn.returnTypeRef.coneType.isBoolean &&
                    fn.getAnnotationByClassId(external, session)?.getStringArgument(nativeMember) == name.asString()) add(fn)
            }
        }
    }
    fun prepare(file: FirFile) { file.transformChildren(object : FirTransformer<Unit>() {
        override fun <E : FirElement> transformElement(element: E, data: Unit): E {
            element.transformChildren(this, data)
            return element
        }
        override fun transformEqualityOperatorCall(equalityOperatorCall: FirEqualityOperatorCall, data: Unit): FirStatement {
            val eq = transformElement(equalityOperatorCall, data)
            return if (eq.source?.kind == KtRealSourceElementKind && (eq.operation == FirOperation.EQ || eq.operation == FirOperation.NOT_EQ))
                AtBodyResolution(eq) else eq
        }
    }, Unit) }

    private inner class AtBodyResolution(val original: FirEqualityOperatorCall) : FirEqualityOperatorCall() {
        override val source get() = original.source
        override val annotations get() = original.annotations
        @UnresolvedExpressionTypeAccess override val coneTypeOrNull get() = original.coneTypeOrNull
        override val argumentList get() = original.argumentList
        override val calleeReference get() = original.calleeReference
        override val operation get() = original.operation
        override fun replaceConeTypeOrNull(newConeTypeOrNull: ConeKotlinType?) = original.replaceConeTypeOrNull(newConeTypeOrNull)
        override fun replaceAnnotations(newAnnotations: List<FirAnnotation>) = original.replaceAnnotations(newAnnotations)
        override fun replaceArgumentList(newArgumentList: FirArgumentList) = original.replaceArgumentList(newArgumentList)
        override fun replaceCalleeReference(newCalleeReference: FirReference) = original.replaceCalleeReference(newCalleeReference)
        override fun <R,D> acceptChildren(visitor: FirVisitor<R,D>, data: D) = original.acceptChildren(visitor, data)
        override fun <D> transformChildren(transformer: FirTransformer<D>, data: D) = apply { original.transformChildren(transformer, data) }
        override fun <D> transformAnnotations(transformer: FirTransformer<D>, data: D) = apply { original.transformAnnotations(transformer, data) }
        override fun <D> transformCalleeReference(transformer: FirTransformer<D>, data: D) = apply { original.transformCalleeReference(transformer, data) }
        @Suppress("UNCHECKED_CAST")
        override fun <E : FirElement,D> transform(transformer: FirTransformer<D>, data: D): E {
            val result = original.transform<FirElement,D>(transformer, data)
            if (result !== original) return result as E
            if (transformer !is FirAbstractBodyResolveTransformer || data !is ResolutionMode) return this as E
            return select(original, transformer) as E
        }
    }

    private fun select(eq: FirEqualityOperatorCall, resolver: FirAbstractBodyResolveTransformer): FirStatement {
            val session = resolver.session
            val name = when (eq.operation) {
                FirOperation.EQ -> Name.identifier("op_Equality")
                FirOperation.NOT_EQ -> Name.identifier("op_Inequality")
                else -> return eq
            }
            val arguments = eq.argumentList.arguments
        if (arguments.any { !it.hasResolvedType || it.resolvedType.canBeNull(session) }) return eq
            val functions = arguments.flatMap { arg ->
                arg.resolvedType.toRegularClassSymbol(session)?.let { candidates(session, it, name) }.orEmpty()
            }.distinct()
            if (functions.isEmpty()) return eq
            val scope = functions.fold(FirLocalScope(session)) { s, f -> s.storeFunction(f, session) }
            return resolver.context.withTowerDataContext(resolver.context.towerDataContext.addLocalScope(scope)) {
                val call = buildFunctionCall {
                    source = eq.source
                    argumentList = buildArgumentList { this.arguments.addAll(arguments) }
                    calleeReference = buildSimpleNamedReference { source = eq.source; this.name = name }
                }
                val mode = ResolutionMode.ContextIndependent
                val resolved = resolver.components.callResolver.resolveCallAndSelectCandidate(call, mode)
                val ref = resolved.calleeReference
            val ambiguity = (ref as? FirErrorReferenceWithCandidate)?.diagnostic as? ConeAmbiguityError
            val ambiguous = ambiguity?.applicability?.isSuccess == true &&
                ambiguity.candidateSymbols.all { target -> functions.any { it.symbol == target } }
            if (!ambiguous && (ref !is FirNamedReferenceWithCandidate || !ref.candidate.isSuccessful ||
                functions.none { it.symbol == ref.candidate.symbol })) return@withTowerDataContext eq
                val flow = resolver.components.dataFlowAnalyzer
                flow.enterCallArguments(resolved, emptyList())
                flow.exitCallExplicitReceiver()
                flow.exitCallArguments()
                flow.enterFunctionCall(resolved)
                resolver.components.callCompleter.completeCall(resolved, mode).also { flow.exitFunctionCall(it, true) }
            }
    }
}
