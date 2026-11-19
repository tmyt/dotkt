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
import org.jetbrains.kotlin.fir.symbols.impl.FirTypeParameterSymbol
import org.jetbrains.kotlin.fir.types.*
import org.jetbrains.kotlin.fir.visitors.FirTransformer
import org.jetbrains.kotlin.fir.visitors.FirVisitor
import org.jetbrains.kotlin.KtRealSourceElementKind
import org.jetbrains.kotlin.KtFakeSourceElementKind
import org.jetbrains.kotlin.GeneratedDeclarationKey
import org.jetbrains.kotlin.descriptors.EffectiveVisibility
import org.jetbrains.kotlin.descriptors.Modality
import org.jetbrains.kotlin.descriptors.Visibilities
import org.jetbrains.kotlin.fir.expressions.impl.buildSingleExpressionBlock
import org.jetbrains.kotlin.fir.builder.generateResolvedAccessExpression
import org.jetbrains.kotlin.fir.declarations.builder.buildProperty
import org.jetbrains.kotlin.fir.declarations.impl.FirResolvedDeclarationStatusImpl
import org.jetbrains.kotlin.fir.moduleData
import org.jetbrains.kotlin.fir.symbols.impl.FirLocalPropertySymbol
import org.jetbrains.kotlin.fir.types.builder.buildResolvedTypeRef
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.Name
import org.jetbrains.kotlin.resolve.calls.tower.isSuccess
import org.jetbrains.kotlin.types.ConstantValueKind
import org.jetbrains.kotlin.types.SmartcastStability

/** Select while the surrounding body is being resolved: a native predicate's true result
 * proves nothing about the operand types. Replacing equality after DFA would leave invalid smart casts. */
internal class NativeEquality {
    private object LiftedOperand : GeneratedDeclarationKey()
    private val external = ClassId.topLevel(FqName("kotlin.clr.ClrExternal"))
    private val cache = mutableMapOf<Pair<FirRegularClassSymbol, Name>, List<FirNamedFunction>>()
    private fun candidates(session: FirSession, owner: FirRegularClassSymbol, name: Name): List<FirNamedFunction> = cache.getOrPut(owner to name) {
        val types = listOf(owner) + lookupSuperTypes(owner.fir, lookupInterfaces = false, deep = true,
            useSiteSession = session, substituteTypes = false).mapNotNull { it.lookupTag.toRegularClassSymbol(session) }
        buildList {
            for (type in types.filter { it.hasAnnotation(external, session) }) session.declaredMemberScope(type, FirResolvePhase.STATUS).processFunctionsByName(name) {
                val fn = it.fir
                if (fn.isStatic && fn.returnTypeRef.coneType.isBoolean &&
                    fn.operatorRole(session) == (if (name.asString() == "op_Equality") "equals" else "notEquals")) add(fn)
            }
        }
    }
    private fun owners(type: ConeKotlinType, session: FirSession, seen: MutableSet<FirTypeParameterSymbol>): List<FirRegularClassSymbol> =
        when (val rigid = type.lowerBoundIfFlexible()) {
            is ConeTypeParameterType -> rigid.lookupTag.typeParameterSymbol.let { parameter ->
                if (seen.add(parameter)) parameter.resolvedBounds.flatMap { owners(it.coneType, session, seen) } else emptyList()
            }
            is ConeIntersectionType -> rigid.intersectedTypes.flatMap { owners(it, session, seen) }
            is ConeDefinitelyNotNullType -> owners(rigid.original, session, seen)
            else -> listOfNotNull(rigid.toRegularClassSymbol(session))
        }
    fun prepare(file: FirFile) { file.transformChildren(object : FirTransformer<Unit>() {
        override fun <E : FirElement> transformElement(element: E, data: Unit): E {
            element.transformChildren(this, data)
            return element
        }
        override fun transformEqualityOperatorCall(equalityOperatorCall: FirEqualityOperatorCall, data: Unit): FirStatement {
            val eq = transformElement(equalityOperatorCall, data)
            return if ((eq.source?.kind == KtRealSourceElementKind || eq.source?.kind == KtFakeSourceElementKind.WhenCondition)
                && (eq.operation == FirOperation.EQ || eq.operation == FirOperation.NOT_EQ))
                AtBodyResolution(eq) else eq
        }
    }, Unit) }

    private inner class AtBodyResolution(val original: FirEqualityOperatorCall) : FirEqualityOperatorCall() {
        // The built-in equality resolver approximates integer literals to Int without an expected
        // type. Keep their original form so ordinary overload resolution can choose a native parameter.
        private val literals = original.argumentList.arguments.map { arg ->
            (arg as? FirLiteralExpression)?.takeIf { it.kind == ConstantValueKind.IntegerLiteral ||
                it.kind == ConstantValueKind.UnsignedIntegerLiteral }?.let {
                buildLiteralExpression(it.source, it.kind, it.value, it.annotations.toMutableList(), setType = false, prefix = it.prefix)
            }
        }
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
            return select(original, transformer, literals) as E
        }
    }

    private fun select(eq: FirEqualityOperatorCall, resolver: FirAbstractBodyResolveTransformer, literals: List<FirLiteralExpression?>): FirStatement {
        val session = resolver.session
        val name = Name.identifier(if (eq.operation == FirOperation.EQ) "op_Equality" else "op_Inequality")
        val args = eq.argumentList.arguments
        if (args.any { !it.hasResolvedType || it is FirLiteralExpression && it.kind == ConstantValueKind.Null }) return eq
        val functions = args.flatMap { arg ->
            owners(arg.resolvedType, session, mutableSetOf()).flatMap { candidates(session, it, name) }
        }.distinct()
        if (functions.isEmpty()) return eq

        val operands = Operands(session, eq)
        val scope = functions.fold(FirLocalScope(session)) { s, f -> s.storeFunction(f, session) }
        return resolver.context.withTowerDataContext(resolver.context.towerDataContext.addLocalScope(scope)) {
            val call = buildFunctionCall {
                source = eq.source
                argumentList = buildArgumentList {
                    for (i in args.indices) arguments += literals[i]?.transform<FirExpression, ResolutionMode>(
                        resolver, ResolutionMode.ContextDependent) ?: operands.present(i)
                }
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
            val selected = resolver.components.callCompleter.completeCall(resolved, mode)
            flow.exitFunctionCall(selected, true)
            operands.guard(selected)
        }
    }

    private class Operands(private val session: FirSession, private val eq: FirEqualityOperatorCall) {
        private val args = eq.argumentList.arguments
        private val nullable = args.map { it.resolvedType.canBeNull(session) }
        // Capture both sides before any null test. A literal has no effects and its original
        // integer-literal type must remain available to the overload resolver.
        private val temps = args.mapIndexed { i, arg ->
            if (nullable.none { it } || arg is FirLiteralExpression) null else buildProperty {
                moduleData = session.moduleData
                origin = LiftedOperand.origin
                name = Name.special(if (i == 0) "<equality-left>" else "<equality-right>")
                symbol = FirLocalPropertySymbol()
                isLocal = true
                isVar = false
                returnTypeRef = buildResolvedTypeRef { coneType = arg.resolvedType }
                status = FirResolvedDeclarationStatusImpl(Visibilities.Local, Modality.FINAL, EffectiveVisibility.Local)
                initializer = arg
            }
        }
        private fun read(i: Int): FirExpression = temps[i]?.let { temp ->
            generateResolvedAccessExpression(null, temp).also { it.replaceConeTypeOrNull(temp.returnTypeRef.coneType) }
        } ?: args[i]

        fun present(i: Int): FirExpression {
            if (!nullable[i]) return read(i)
            val target = args[i].resolvedType.lowerBoundIfFlexible().makeConeTypeDefinitelyNotNullOrNotNull(session.typeContext)
            // Only the private val is proven non-null in the guarded arm, never the user's
            // expression. An explicit Kotlin cast would incorrectly diagnose generic T & Any.
            return buildSmartCastExpression {
                coneTypeOrNull = target
                originalExpression = read(i)
                upperTypesFromSmartCast = listOf(target)
                lowerTypesFromSmartCast = emptyList()
                smartcastType = buildResolvedTypeRef { coneType = target }
                smartcastStability = SmartcastStability.STABLE_VALUE
            }
        }

        fun guard(selected: FirExpression): FirExpression {
            if (nullable.none { it }) return selected
            val boolean = session.builtinTypes.booleanType.coneType
            val equality = eq.operation == FirOperation.EQ
            fun truth(value: Boolean) = buildLiteralExpression(null, ConstantValueKind.Boolean, value, mutableListOf(), setType = true)
            fun nullCheck(i: Int, equal: Boolean) = buildEqualityOperatorCall {
                operation = if (equal) FirOperation.IDENTITY else FirOperation.NOT_IDENTITY
                argumentList = buildArgumentList {
                    arguments += read(i)
                    arguments += buildLiteralExpression(null, ConstantValueKind.Null, null, mutableListOf(), setType = true)
                }
            }.also { it.replaceConeTypeOrNull(boolean) }
            fun branch(test: FirExpression, value: FirExpression) = buildRegularWhenBranch {
                condition = test
                result = buildSingleExpressionBlock(value).also { it.replaceConeTypeOrNull(boolean) }
            }
            val guarded = buildWhenExpression {
                coneTypeOrNull = boolean
                usedAsExpression = true
                exhaustivenessStatus = ExhaustivenessStatus.ProperlyExhaustive
                if (nullable[0]) branches += branch(nullCheck(0, true),
                    if (nullable[1]) nullCheck(1, equality) else truth(!equality))
                if (nullable[1]) branches += branch(nullCheck(1, true), truth(!equality))
                branches += branch(buildElseIfTrueCondition(), selected)
            }
            // A raw FIR block is a statement container, not a legal operand in all
            // expression positions (notably &&). Give the temporaries a branch scope.
            return buildWhenExpression {
                source = eq.source
                coneTypeOrNull = boolean
                usedAsExpression = true
                exhaustivenessStatus = ExhaustivenessStatus.ProperlyExhaustive
                branches += buildRegularWhenBranch {
                    condition = buildElseIfTrueCondition()
                    result = buildBlock {
                        coneTypeOrNull = boolean
                        statements += temps.filterNotNull()
                        statements += guarded
                    }
                }
            }
        }
    }
}
