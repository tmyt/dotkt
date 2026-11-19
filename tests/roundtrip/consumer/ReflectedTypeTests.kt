package roundtriptests.reflectedtypes

import NUnit.Framework.TestAttribute
import kotlin.reflect.KTypeProjection
import kotlin.reflect.KTypeParameter
import kotlin.reflect.KVariance
import kotlin.reflect.typeOf
import roundtrip.reflectedtypes.reflected
import roundtrip.reflectedtypes.reflectedList
import roundtrip.reflectedtypes.reflectedInLambda

private class Box<T>
private fun <T : CharSequence> boundedType() = typeOf<List<T?>>()
private fun <T : CharSequence> forwardedType() = reflected<List<T?>>()
private class DeclaringClass<out E> { fun element() = typeOf<List<E>>() }

class ReflectedTypeTests {
    @TestAttribute fun ordinaryParametersBelongToTheirDeclaration() {
        val element = boundedType<String>().arguments.single().type!!
        val t = element.classifier as KTypeParameter
        check(element.isMarkedNullable && t.name == "T" && !t.isReified)
        check(t.upperBounds.single().classifier == CharSequence::class && t.variance == KVariance.INVARIANT)
        check(t.upperBounds.single() == typeOf<CharSequence>())
        check(reflected<List<CharSequence?>>().arguments.single().type == typeOf<CharSequence?>())
        check(t == boundedType<StringBuilder>().arguments.single().type!!.classifier)
        val forwarded = forwardedType<String>().arguments.single().type!!
        check(forwarded.isMarkedNullable && forwarded.classifier is KTypeParameter && forwarded.classifier != t)
        val e = DeclaringClass<Any?>().element().arguments.single().type!!
        check(!e.isMarkedNullable)
        val classifier = e.classifier as KTypeParameter
        check(classifier.variance == KVariance.OUT && classifier.upperBounds.single() == typeOf<Any?>())
    }

    @TestAttribute fun nestedTypeSurvivesReifiedLibraryCalls() {
        val direct = typeOf<List<String?>>()
        val imported = reflected<List<String?>>()
        val composed = reflectedList<String>()
        check(direct == imported && direct == composed)
        check(direct == reflectedInLambda<String>()())
        check(direct.toString() == "kotlin.collections.List<kotlin.String?>")
        check(direct.classifier == List::class && !direct.isMarkedNullable)
        val arg = direct.arguments.single()
        check(arg.variance == KVariance.INVARIANT)
        check(arg.type!!.classifier == String::class && arg.type!!.isMarkedNullable)
        check(direct != reflected<List<String>>())
    }

    @TestAttribute fun useSiteProjectionsAndOuterNullabilitySurvive() {
        val star = reflected<Box<*>?>()
        check(star.isMarkedNullable && star.classifier == Box::class)
        check(star.arguments.single() == KTypeProjection.STAR)
        val covariant = reflected<Box<out String>>().arguments.single()
        val contravariant = reflected<Box<in Int>>().arguments.single()
        check(covariant.variance == KVariance.OUT && covariant.type!!.classifier == String::class)
        check(contravariant.variance == KVariance.IN && contravariant.type!!.classifier == Int::class)
        val function = reflected<(String) -> Int>()
        check(function.classifier == Function1::class)
        check(function.arguments.map { it.type!!.classifier } == listOf(String::class, Int::class))
        val projectedFunction = reflected<Function1<*, Int>>()
        check(projectedFunction.classifier == Function1::class)
        check(projectedFunction.arguments == listOf(KTypeProjection.STAR, KTypeProjection.invariant(typeOf<Int>())))
        check(typeOf<Function1<*, *>>().arguments == listOf(KTypeProjection.STAR, KTypeProjection.STAR))
    }
}
