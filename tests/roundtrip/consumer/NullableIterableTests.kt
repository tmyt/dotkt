package roundtriptests.nullableiterable

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.nullableiterable.*

private class LocalBag<A, T>(val items: List<T>) : Iterable<T> {
    override fun iterator(): Iterator<T> = items.iterator()
}

class NullableIterableTests {
    @TestAttribute
    fun nullableElementsFollowTheImplementedIterableType() {
        assertEquals(3, countPresent<Int>(0, 1..3))
        assertEquals(3, countPresent<Long>(0, 1L..3L))
        assertEquals(3, countPresent<Char>(0, 'a'..'c'))
        assertEquals(2, countPresent<Int>(0, listOf(1, 2)))
        assertEquals(2, countPresent<Int>(0, arrayListOf(1, 2)))
        assertEquals(2, countPresent<Int>(0, LocalBag<String, Int>(listOf(1, 2))))
        assertEquals(2, countPresent<Int>(0, Bag<String, Int>(listOf(1, 2))))
        assertEquals(2, countPresent<Int>(0, DerivedBag<Int>(listOf(1, 2))))
        assertEquals(2, countViaOpenElement<Int>(DerivedBag<Int>(listOf(1, 2))))
        assertEquals(2, countPresent<Int>(0, listOf<Int?>(1, null, 2)))
        assertEquals(2, countPresent<String>(0, DerivedBag<String>(listOf("a", "b"))))
    }
}
