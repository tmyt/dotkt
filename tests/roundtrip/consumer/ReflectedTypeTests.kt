package roundtriptests.reflectedtypes

import NUnit.Framework.TestAttribute
import kotlin.reflect.KTypeProjection
import kotlin.reflect.KTypeParameter
import kotlin.reflect.KType
import kotlin.reflect.KFunction
import kotlin.reflect.KFunction1
import kotlin.reflect.KVariance
import kotlin.reflect.typeOf
import roundtrip.reflectedtypes.reflected
import roundtrip.reflectedtypes.reflectedList
import roundtrip.reflectedtypes.reflectedInLambda
import roundtrip.reflectedtypes.LibraryBox
import roundtrip.reflectedtypes.declaredBox
import roundtrip.reflectedtypes.defaultType
import roundtrip.reflectedtypes.reflectAfter
import roundtrip.reflectedtypes.declaredInCallback
import roundtrip.reflectedtypes.declaredInDefault

private class Box<T>
private inline fun <reified T> forwardedDefault() = defaultType<T>()
private fun <T : CharSequence> boundedType() = typeOf<List<T?>>()
private inline fun <reified R, T : R> dependentBound() = typeOf<List<T>>()
private fun <T : CharSequence> forwardedType() = reflected<List<T?>>()
private class DeclaringClass<out E> { fun element() = typeOf<List<E>>() }
private class Enclosing<E> {
    fun element() = typeOf<List<E>>()
    inner class Inner { fun sameElement() = typeOf<List<E>>() }
}

class ReflectedTypeTests {
    @TestAttribute fun ordinaryParametersBelongToTheirDeclaration() {
        fun <T> local() = typeOf<List<T>>()
        fun parameter(shape: KType) = shape.arguments.single().type!!.classifier as KTypeParameter
        val localT = parameter(local<Int>())
        val callbackT = parameter(declaredInCallback<String> { it })
        val stored: (KType) -> KType = { it }
        val defaultT = parameter(declaredInDefault<String>())
        check(localT.name == "T" && localT == parameter(local<String>()))
        check(callbackT == parameter(declaredInCallback<Int>(stored)) && defaultT == parameter(declaredInDefault<Int>()))
        check(localT != callbackT && callbackT != defaultT && localT != defaultT)
        val dependent = dependentBound<Any, String>().arguments.single().type!!.classifier as KTypeParameter
        val upper = dependent.upperBounds.single().classifier as KTypeParameter
        check(upper.name == "R" && upper.isReified && upper.upperBounds.single() == typeOf<Any?>())
        val outer = Enclosing<String>()
        check(outer.element() == outer.Inner().sameElement())
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
        check(declaredBox() == typeOf<LibraryBox<String>>())
        check(forwardedDefault<String>() == typeOf<List<String>>())
        var calls = 0
        check(reflectAfter<CharSequence> { calls++ } == typeOf<CharSequence>() && calls == 1)
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
        val callable = typeOf<KFunction1<String, Int>>()
        check(callable != function && callable.arguments == function.arguments)
        check(callable.classifier == KFunction1::class && callable.classifier == KFunction::class)
        check(callable.classifier != function.classifier && reflected<KFunction1<String, Int>>() == callable)
        check(reflectAfter<List<KFunction1<String, Int>>> {}.arguments.single().type == callable)
    }
}
