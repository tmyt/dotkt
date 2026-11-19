package DotKt.Runtime.CompilerServices

import kotlin.reflect.ClrReflectedType
import kotlin.reflect.ClrDeclaredParameter
import kotlin.reflect.KClassifier
import kotlin.reflect.KType
import kotlin.reflect.KTypeProjection
import kotlin.reflect.KVariance

@PublishedApi
internal fun reflectedType(name: String, classifier: KClassifier?, nullable: Boolean, arguments: Array<KTypeProjection>): KType =
    ClrReflectedType(name, classifier, arguments.asList(), nullable)

@PublishedApi
internal fun reflectedParameter(owner: Any, declaration: String, index: Int, name: String,
                               variance: String, nullable: Boolean, bounds: Array<KType>): KType {
    val parameter = ClrDeclaredParameter((owner as StarProjectionType).module, declaration, index, name,
        when (variance) { "in" -> KVariance.IN; "out" -> KVariance.OUT; else -> KVariance.INVARIANT },
        bounds.asList())
    return ClrReflectedType(name, parameter, emptyList(), nullable)
}

@PublishedApi internal fun reflectedStar(): KTypeProjection = KTypeProjection.STAR
@PublishedApi internal fun reflectedInvariant(type: KType): KTypeProjection = KTypeProjection.invariant(type)
@PublishedApi internal fun reflectedIn(type: KType): KTypeProjection = KTypeProjection.contravariant(type)
@PublishedApi internal fun reflectedOut(type: KType): KTypeProjection = KTypeProjection.covariant(type)

@PublishedApi internal fun reflectedWitnessType(value: Any, nullable: Boolean): KType {
    val type = value as ClrReflectedType
    return if (nullable && !type.isMarkedNullable) type.copy(isMarkedNullable = true) else type
}
