package roundtriptests.heterogeneousidentity

import NUnit.Framework.TestAttribute
import roundtrip.heterogeneousidentity.*

private fun <T, U : T> throughInline(a: T, b: U): Boolean = inlinePair(a, b)
private fun <T, U : T> throughSplice(a: T, b: U): Boolean {
    var calls = 0
    val result = splicedPair(a, b) { calls++ }
    check(calls == 1)
    return result
}

class HeterogeneousReferenceEqualityTests {
    @TestAttribute
    fun distinctGenericSlotsPreserveReferenceIdentity() {
        val first = Child()
        val second = Child()
        check(first == second)
        check(pairSame<Node, Child>(first, first))
        check(pairReversed<Node, Child>(first, first))
        check(!pairDifferent<Node, Child>(first, first))
        check(!pairSame<Node, Child>(first, second))
        check(!pairReversed<Node, Child>(first, second))
        check(pairDifferent<Node, Child>(first, second))
        check(boundSame(first, first) && boundReversed(first, first))
        check(!boundSame(first, second) && !boundReversed(first, second))
        check(throughInline<Node, Child>(first, first))
        check(!throughInline<Node, Child>(first, second))
        check(throughSplice<Node, Child>(first, first))
        check(!throughSplice<Node, Child>(first, second))
        check(PairHolder<Node, Child>(first, first).same())
        check(!PairHolder<Node, Child>(first, second).reversed())
        check(caughtIdentity())
        check(!genericNull(first) && !genericNullReversed(first))
    }

    @TestAttribute
    fun heterogeneousValuesBoxWithoutChangingNumericComparisons() {
        check(nullablePrimitive(null, null))
        check(nullableReversed(null, null))
        check(!nullablePrimitive(null, 7))
        check(!nullableReversed(7, null))
        val boxed: Any = 1000
        check(!nullablePrimitive(boxed, 1000))
        check(!nullableReversed(1000, boxed))
        check(pairSame<Any, Any>(boxed, boxed))
        check(!pairSame<Int, Int>(1000, 1000))
        check(!pairReversed<Int, Int>(1000, 1000))
        check(homogeneous(1000, 1000))
        check(computedBoolean(false, true) && computedBooleanReversed(false, true))
        check(!computedBoolean(true, true) && !computedBooleanReversed(true, true))
        check(genericNull<Int?>(null) && genericNullReversed<Int?>(null))
        check(!genericNull(7) && !genericNullReversed(7))
        check(ComparedDerived(null, null).same)
        check(!ComparedDerived(null, 7).same)
        val one = 1
        val anotherOne = 1
        check(one == anotherOne)
        val nan = Double.NaN
        check(nan != nan)
        check(-0.0 == 0.0)
    }
}
