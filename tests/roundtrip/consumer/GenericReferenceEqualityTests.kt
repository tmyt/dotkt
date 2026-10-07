package roundtriptests.genericreferenceequality

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.genericreferenceequality.*

private class EqualValue {
    override fun equals(other: Any?): Boolean = other is EqualValue
    override fun hashCode(): Int = 1
}
private fun <T> throughInline(a: T?, b: T): Boolean = inlineSame(a, b)
private fun <T> throughSplice(a: T?, b: T): Boolean {
    var calls = 0
    val result = splicedSame(a, b) { calls++ }
    assertTrue(calls == 1)
    return result
}
private fun concreteRawSplice(stop: Boolean): Boolean {
    return splicedRawSame<Int>(1000, 1000) { if (stop) return false }
}
private fun concreteNullableSplice(stop: Boolean): Boolean {
    return splicedNullableSame<Int>(1000, 1000) { if (stop) return false }
}
private fun concreteStructSplice(stop: Boolean): Boolean {
    return splicedRawSame(System.ValueTuple2<Int, Int>(1, 2), System.ValueTuple2<Int, Int>(1, 3)) {
        if (stop) return false
    }
}

class GenericReferenceEqualityTests {
    @TestAttribute
    fun nullableValueOperandOrderIsVerifiable() {
        assertFalse(nullableFirst<Int>(null, 7))
        assertFalse(nullableLast<Int>(7, null))
        assertTrue(different<Int>(null, 7))
        assertFalse(nullableFirst<Int>(7, 7))
        assertFalse(nullableLast<Int>(7, 7))
        assertFalse(throughInline<Int>(null, 7))
        assertFalse(throughSplice<Int>(null, 7))
        assertFalse(throughSplice<Int>(7, 7))
        assertFalse(Holder<Int>(null, 7).same())
        assertFalse(Holder<Int>(null, 7).reversed())
        assertTrue(rawSame(1000, 1000))
        assertTrue(concreteRawSplice(false))
        assertFalse(concreteNullableSplice(false))
        assertFalse(concreteStructSplice(false))
        assertFalse(rawSame(1000, 1001))
        assertTrue(rawSame(-0.0, 0.0))
        assertFalse(rawSame(Double.NaN, Double.NaN))
        assertTrue(rawSame(Long.MAX_VALUE, Long.MAX_VALUE))
        assertFalse(rawSame(Long.MAX_VALUE, Long.MIN_VALUE))
        assertTrue(rawSame(IdentityColor.RED, IdentityColor.RED))
        assertFalse(rawSame(IdentityColor.RED, IdentityColor.BLUE))
        assertTrue(rawSame(System.Reflection.BindingFlags.Instance, System.Reflection.BindingFlags.Instance))
        assertFalse(rawSame(System.Reflection.BindingFlags.Instance, System.Reflection.BindingFlags.Static))
        assertTrue(IdentityHolder<Int>().same(1000, 1000))
        var evaluations = 0
        assertTrue(effectsSame({ evaluations++; 1000 }, { evaluations++; 1000 }))
        assertTrue(evaluations == 2)
        val firstPair = System.ValueTuple2<Int, Int>(1, 2)
        val secondPair = System.ValueTuple2<Int, Int>(1, 3)
        assertFalse(rawSame(firstPair, secondPair))
        assertFalse(localCondition<Int>(null, 7))
        assertFalse(localCondition<Int>(7, 7))
        assertFalse(callResults<Int>(null, 7))
        assertFalse(callResults<Int>(7, 7))
        assertTrue(projectedResult(Result.success(1000), 1000))
    }
    @TestAttribute
    fun referenceComparisonDoesNotCallEquals() {
        val first = EqualValue()
        val second = EqualValue()
        assertTrue(nullableFirst(first, first))
        assertTrue(nullableLast(first, first))
        assertFalse(different(first, first))
        assertFalse(nullableFirst(first, second))
        assertFalse(nullableLast(first, second))
        assertTrue(different(first, second))
        assertTrue(throughInline(first, first))
        assertFalse(throughInline(first, second))
        assertTrue(throughSplice(first, first))
        assertFalse(throughSplice(first, second))
        assertTrue(Holder(first, first).same())
        assertFalse(Holder(first, second).reversed())
        assertTrue(localCondition(first, first))
        assertFalse(localCondition(first, second))
        assertTrue(callResults(first, first))
        assertFalse(callResults(first, second))
        assertTrue(projectedResult(Result.success(first), first))
        assertFalse(projectedResult(Result.success(first), second))
    }
    @TestAttribute
    fun nullAndExistingBoxReferencesRetainIdentity() {
        assertTrue(nullableFirst<Int?>(null, null))
        assertTrue(nullableLast<Int?>(null, null))
        assertFalse(different<Int?>(null, null))
        assertTrue(throughSplice<Int?>(null, null))
        val boxed: Any = 1000
        assertTrue(nullableFirst<Any>(boxed, boxed))
        assertTrue(nullableLast<Any>(boxed, boxed))
        assertFalse(nullableFirst<Any>(null, boxed))
        assertFalse(nullableLast<Any>(boxed, null))
        assertTrue(rawSame<Any>(boxed, boxed))
        val text: Any = "does-not-inhabit-Int"
        assertTrue(uncheckedSame<Int>(text, text))
        assertFalse(uncheckedSame<Int>(text, boxed))
    }
}
