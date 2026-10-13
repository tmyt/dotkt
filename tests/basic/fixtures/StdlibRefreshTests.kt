@file:OptIn(kotlin.ExperimentalStdlibApi::class, kotlin.ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

class StdlibRefreshTests {
    @TestAttribute
    fun metadata_constants_retain_their_values() {
        assertEquals(-2147483648, Int.MIN_VALUE)
        assertTrue(Double.POSITIVE_INFINITY > Double.MAX_VALUE)
        assertTrue(Double.NEGATIVE_INFINITY < -Double.MAX_VALUE)
    }

    @TestAttribute
    fun distinct_elements_cover_generic_and_byte_domain_storage() {
        assertTrue(emptyList<Int>().allDistinct())
        assertTrue(listOf("one", "two").allDistinct())
        assertFalse(arrayOf("one", "one").allDistinct())
        assertTrue(sequenceOf(1, 2, 3).allDistinct())
        assertFalse(intArrayOf(1, 2, 1).allDistinct())
        assertTrue(ubyteArrayOf(0u, 63u, 64u, 127u, 128u, 191u, 192u, 255u).allDistinct())
        assertFalse(ubyteArrayOf(0u, 64u, 255u, 64u).allDistinct())
    }

    @TestAttribute
    fun selectors_run_once_per_visited_element() {
        var calls = 0
        assertTrue(listOf(1, 2, 3).isSortedBy { calls++; it })
        assertEquals(3, calls)
        calls = 0
        assertTrue(listOf("aa", "bb", "cc").allEqualBy { calls++; it.length })
        assertEquals(3, calls)
        calls = 0
        assertTrue(listOf("a", "bb", "ccc").allDistinctBy { calls++; it.length })
        assertEquals(3, calls)
    }
}
