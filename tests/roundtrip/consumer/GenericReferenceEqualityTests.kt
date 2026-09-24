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
        assertFalse(localCondition<Int>(null, 7))
        assertFalse(localCondition<Int>(7, 7))
        assertFalse(callResults<Int>(null, 7))
        assertFalse(callResults<Int>(7, 7))
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
    }
}
