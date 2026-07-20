// Ktp collections battery — migrates the single-project cases/ktproj-coll .ktproj sample onto the in-process NUnit
// suite. The old case was a PRACTICAL collections app consuming the real CLR stdlib (DotKt.Stdlib.dll) through
// MSBuild; the in-process tests/il suite IS exactly that shape (a DotKt app compiled against the rt stdlib), so the
// case migrates verbatim to one @TestAttribute method whose per-value asserts are strictly stronger (typed) than the
// old stdout diff. Every value the old ktproj golden asserted is preserved 1:1 (see the `// <expected>` comments).
//
// It exercises the "app consumes the rt stdlib" path: a `List` held as a local (resolves as the referenced
// IReadOnlyList), member access (size / indexing), TOP-LEVEL stdlib funs (first / getOrElse / contains / indexOf /
// count / isEmpty / take) which kotc emits as `callStatic owner=null` and bir2cir attributes to their file-class owner
// (kotlin.collections._CollectionsKt), AND `for (x in list)` (the iterator protocol re-pointed at the real referenced
// kotlin.collections.Iterator<E> via the rt bridge).
//
// Coverage preserved (old case -> method):
//   ktproj-coll  -> coll_appConsumesRtStdlib  List local / size / index / first / getOrElse / contains / indexOf /
//                                              count / isEmpty / take / for-loop sum / first().uppercase() / for-loop lengths
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse

class MigratedKtpCollectionsTests {
    @TestAttribute
    fun coll_appConsumesRtStdlib() {
        val nums = listOf(10, 20, 30, 40, 50)
        assertEquals(5, nums.size)                    // 5
        assertEquals(30, nums[2])                     // 30
        assertEquals(10, nums.first())                // 10   (top-level fun -> _CollectionsKt.first)
        assertEquals(20, nums.getOrElse(1) { -1 })    // 20   (top-level fun, in range)
        assertEquals(-1, nums.getOrElse(10) { -1 })   // -1   (out of range -> default lambda)
        assertTrue(nums.contains(30))                 // True
        assertEquals(3, nums.indexOf(40))             // 3
        assertEquals(5, nums.count())                 // 5
        assertFalse(nums.isEmpty())                   // False
        assertEquals(2, nums.take(2).size)            // 2

        var total = 0                                 // for-loop over the List (iterator protocol resolves to the real
        for (n in nums) total += n                    // referenced kotlin.collections.Iterator<Int>, via the rt bridge)
        assertEquals(150, total)                      // 150

        val words = listOf("apple", "pear", "fig")
        assertEquals("APPLE", words.first().uppercase())  // APPLE
        assertEquals("pear", words[1])                    // pear
        val lengths = mutableListOf<Int>()
        for (w in words) lengths.add(w.length)
        assertEquals(listOf(5, 4, 3), lengths)            // 5 / 4 / 3
    }
}
