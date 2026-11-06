@file:OptIn(org.jetbrains.kotlin.fir.symbols.SymbolInternals::class, org.jetbrains.kotlin.fir.SessionConfiguration::class)

package kotc.pipeline

import org.jetbrains.kotlin.fir.FirSession
import org.jetbrains.kotlin.fir.FirElement
import org.jetbrains.kotlin.fir.containingClassLookupTag
import org.jetbrains.kotlin.fir.exceptionHandler
import org.jetbrains.kotlin.fir.declarations.FirFile
import org.jetbrains.kotlin.fir.declarations.FirRegularClass
import org.jetbrains.kotlin.fir.declarations.FirResolvePhase
import org.jetbrains.kotlin.fir.declarations.fullyExpandedClass
import org.jetbrains.kotlin.fir.declarations.builder.FirRegularClassBuilder
import org.jetbrains.kotlin.fir.expressions.FirOperation
import org.jetbrains.kotlin.fir.expressions.FirQualifiedAccessExpression
import org.jetbrains.kotlin.fir.expressions.FirCallableReferenceAccess
import org.jetbrains.kotlin.fir.expressions.FirResolvedQualifier
import org.jetbrains.kotlin.fir.expressions.builder.buildArgumentList
import org.jetbrains.kotlin.fir.expressions.builder.buildTypeOperatorCall
import org.jetbrains.kotlin.fir.expressions.builder.buildResolvedQualifier
import org.jetbrains.kotlin.fir.references.FirResolvedNamedReference
import org.jetbrains.kotlin.fir.pipeline.runResolution
import org.jetbrains.kotlin.fir.resolve.ScopeSession
import org.jetbrains.kotlin.fir.resolve.lookupSuperTypes
import org.jetbrains.kotlin.fir.resolve.providers.symbolProvider
import org.jetbrains.kotlin.fir.resolve.toSymbol
import org.jetbrains.kotlin.fir.resolve.transformers.FirGlobalResolveProcessor
import org.jetbrains.kotlin.fir.resolve.transformers.FirResolveProcessor
import org.jetbrains.kotlin.fir.resolve.transformers.FirTransformerBasedResolveProcessor
import org.jetbrains.kotlin.fir.resolve.transformers.createAllCompilerResolveProcessors
import org.jetbrains.kotlin.fir.symbols.impl.FirRegularClassSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirCallableSymbol
import org.jetbrains.kotlin.fir.symbols.impl.FirTypeParameterSymbol
import org.jetbrains.kotlin.fir.types.ConeCapturedType
import org.jetbrains.kotlin.fir.types.ConeClassLikeType
import org.jetbrains.kotlin.fir.types.ConeDefinitelyNotNullType
import org.jetbrains.kotlin.fir.types.ConeIntersectionType
import org.jetbrains.kotlin.fir.types.ConeKotlinType
import org.jetbrains.kotlin.fir.types.ConeTypeParameterType
import org.jetbrains.kotlin.fir.types.FirCorrespondingSupertypesCache
import org.jetbrains.kotlin.fir.types.FirTypeRef
import org.jetbrains.kotlin.fir.types.coneType
import org.jetbrains.kotlin.fir.types.constructClassLikeType
import org.jetbrains.kotlin.fir.types.lowerBoundIfFlexible
import org.jetbrains.kotlin.fir.types.resolvedType
import org.jetbrains.kotlin.fir.types.builder.buildResolvedTypeRef
import org.jetbrains.kotlin.fir.visitors.FirDefaultVisitorVoid
import org.jetbrains.kotlin.name.ClassId
import org.jetbrains.kotlin.name.FqName
import org.jetbrains.kotlin.name.StandardClassIds

private val nativeArrayBase = ClassId.topLevel(FqName("System.Array"))

/** Frontend stdlib array classifiers expose one common base, including unsigned and open generic arrays.
 * Install the source assignability edge before FIR overload resolution; do not rewrite a selected
 * parameter, insert an argument conversion, or change any array storage/emission rule.
 * Physical array representation remains owned by bir2cir's BirTypeLowering. */
internal fun FirRegularClassBuilder.addClrArrayBase(classId: ClassId) {
    if (!isClrArray(classId)) return
    superTypeRefs.removeAll { (it.coneType as? ConeClassLikeType)?.lookupTag?.classId == StandardClassIds.Any }
    superTypeRefs += nativeArrayBaseRef()
}

private fun isClrArray(classId: ClassId) = classId == StandardClassIds.Array ||
    classId in StandardClassIds.elementTypeByPrimitiveArrayType ||
    classId in StandardClassIds.elementTypeByUnsignedArrayType

private fun nativeArrayBaseRef() = buildResolvedTypeRef {
    coneType = nativeArrayBase.constructClassLikeType(emptyArray(), false)
}

/** Source array declarations keep their Kotlin inheritance. The native base is an expression
 * assignability relation, not a declaration's supertype or a reference-assembly base class. */
internal fun FirSession.sourceArrayClasses(): List<FirRegularClass> {
    val arrayIds = listOf(StandardClassIds.Array) + StandardClassIds.elementTypeByPrimitiveArrayType.keys +
        StandardClassIds.elementTypeByUnsignedArrayType.keys
    return arrayIds.mapNotNull { id ->
        (symbolProvider.getClassLikeSymbolByClassId(id) as? FirRegularClassSymbol)?.fir
            ?.takeIf { it.origin.fromSource }
    }
}

internal fun FirSession.resolveWithClrArrayRelations(files: List<FirFile>): Pair<ScopeSession, List<FirFile>> {
    val arrays = sourceArrayClasses()
    if (arrays.isEmpty()) return runResolution(files)
    val declarationScopes = ScopeSession()
    val processors = createAllCompilerResolveProcessors(this, declarationScopes)
    val boundary = FirResolvePhase.IMPLICIT_TYPES_BODY_RESOLVE
    processors.filter { checkNotNull(it.phase) < boundary }.forEach { it.processClrFiles(files) }
    withSourceArrayBases(arrays) {
        val bodyScopes = ScopeSession()
        createAllCompilerResolveProcessors(this, bodyScopes).filter { checkNotNull(it.phase) >= boundary }
            .forEach { it.processClrFiles(files) }
    }
    return ScopeSession() to files
}

internal fun FirSession.normalizeClrArrayReceivers(files: List<FirFile>, normalizeViews: () -> Unit) {
    val arrays = sourceArrayClasses()
    if (arrays.isEmpty()) { normalizeViews(); return }
    withSourceArrayBases(arrays) {
        normalizeViews()
        normalizeArrayNativeReceivers(files)
    }
}

internal fun <T> FirSession.withSourceArrayBases(arrays: List<FirRegularClass>,
    action: (Map<FirRegularClass, List<FirTypeRef>>) -> T): T {
    val original = arrays.associateWith { it.superTypeRefs.toList() }
    return withArrayBaseOverrides(original.mapValues { (_, bases) -> bases + nativeArrayBaseRef() }) { action(original) }
}

internal fun <T> FirSession.withArrayBaseOverrides(bases: Map<FirRegularClass, List<FirTypeRef>>, action: () -> T): T {
    val previous = bases.keys.associateWith { it.superTypeRefs.toList() }
    try {
        for ((array, view) in bases) array.replaceSuperTypeRefs(view)
        register(FirCorrespondingSupertypesCache::class, FirCorrespondingSupertypesCache(this))
        return action()
    } finally {
        for ((array, view) in previous) array.replaceSuperTypeRefs(view)
        register(FirCorrespondingSupertypesCache::class, FirCorrespondingSupertypesCache(this))
    }
}

/** Keep the selected native owner explicit after the lookup-only relation is removed. */
@OptIn(org.jetbrains.kotlin.fir.types.ResolvedQualifierTypeAccess::class)
private fun FirSession.normalizeArrayNativeReceivers(files: List<FirFile>) {
    val native = (symbolProvider.getClassLikeSymbolByClassId(nativeArrayBase) as FirRegularClassSymbol).fir
    val owners = lookupSuperTypes(native, lookupInterfaces = true, deep = true,
        useSiteSession = this, substituteTypes = false).mapTo(mutableSetOf()) { it.lookupTag.classId }
    owners += nativeArrayBase
    fun sourceArray(type: ConeKotlinType, seen: MutableSet<Any>): Boolean = when (val rigid = type.lowerBoundIfFlexible()) {
        is ConeClassLikeType -> isClrArray(rigid.lookupTag.classId)
        is ConeTypeParameterType -> (rigid.lookupTag.toSymbol(this) as FirTypeParameterSymbol).let {
            seen.add(it) && it.resolvedBounds.any { bound -> sourceArray(bound.coneType, seen) }
        }
        is ConeCapturedType -> seen.add(rigid.constructor) && rigid.constructor.supertypes.orEmpty().any { sourceArray(it, seen) }
        is ConeIntersectionType -> rigid.intersectedTypes.any { sourceArray(it, seen) }
        is ConeDefinitelyNotNullType -> sourceArray(rigid.original, seen)
        else -> false
    }
    for (file in files) file.accept(object : FirDefaultVisitorVoid() {
        override fun visitElement(element: FirElement) {
            element.acceptChildren(this)
            val access = element as? FirQualifiedAccessExpression ?: return
            val callable = (access.calleeReference as? FirResolvedNamedReference)?.resolvedSymbol as? FirCallableSymbol<*> ?: return
            val owner = callable.containingClassLookupTag()?.classId ?: return
            if (owner !in owners) return
            val qualifier = (access.explicitReceiver as? FirResolvedQualifier)
                ?: (access.dispatchReceiver as? FirResolvedQualifier)
            if (qualifier != null && qualifier.qualifierSymbol?.fullyExpandedClass(this@normalizeArrayNativeReceivers)
                    ?.classId?.let(::isClrArray) == true) {
                val selectedOwner = symbolProvider.getClassLikeSymbolByClassId(owner)
                    ?: error("Missing native array declaration owner $owner")
                val normalized = buildResolvedQualifier {
                    source = qualifier.source
                    coneTypeOrNull = qualifier.resolvedType
                    packageFqName = owner.packageFqName
                    relativeClassFqName = owner.relativeClassName
                    qualifierSymbol = selectedOwner
                    resolvedToCompanionObject = false
                }
                if (access.explicitReceiver === qualifier) access.replaceExplicitReceiver(normalized)
                if (access.dispatchReceiver === qualifier) access.replaceDispatchReceiver(normalized)
                if (access is FirCallableReferenceAccess) {
                    val path = file.sourceFile?.path ?: error("Missing source path for array reference")
                    val source = access.source ?: error("Missing source location for array reference")
                    kotc.frontend.SelectedCallDeclarations.recordReferenceOwner(path, source.endOffset,
                        callable.name.asString(), owner.constructClassLikeType(emptyArray(), false))
                }
                return
            }
            val receiver = access.dispatchReceiver ?: return
            if (!sourceArray(receiver.resolvedType, mutableSetOf())) return
            val target = nativeArrayBase.constructClassLikeType(emptyArray(), false)
            val converted = buildTypeOperatorCall {
                coneTypeOrNull = target
                operation = FirOperation.AS
                conversionTypeRef = buildResolvedTypeRef { coneType = target }
                argumentList = buildArgumentList { arguments += receiver }
            }
            access.replaceDispatchReceiver(converted)
            if (access.explicitReceiver === receiver) access.replaceExplicitReceiver(converted)
        }
    })
}

/** Upstream's resolve-processor orchestration, with the same phase and file exception handling. */
private fun FirResolveProcessor.processClrFiles(files: List<FirFile>) {
    beforePhase()
    try {
        when (this) {
            is FirTransformerBasedResolveProcessor -> for (file in files) {
                try { processFile(file) }
                catch (failure: Throwable) { file.moduleData.session.exceptionHandler.handleExceptionOnFileAnalysis(file, failure) }
            }
            is FirGlobalResolveProcessor -> process(files)
            else -> error("Unknown FIR resolve processor $this")
        }
    } finally { afterPhase() }
}
