// Lazy-sequence + sorting battery (batch M5). Migrates the Sequence/LINQ family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals is strictly stronger (typed) than the old text diff; every asserted value is preserved 1:1
// (see the `// <expected>` comments). These use `asSequence()` (a deferred .NET IEnumerable pass-through) and
// LINQ-backed ordering — pure same-module Kotlin, no `sequence{}`/yield/coroutine.
//
// Coverage preserved (old case -> method):
//   il-seq       -> sequences_lazyChain        asSequence().map/filter/take/takeWhile/dropWhile + terminals (first/count/sum/single/toList)
//   il-seqfilter -> sequences_valueTypeFilter  value-type (Int) FilteringSequence: nextItem:T? erased to object, calcNext boxes the element
//   il-sort      -> sorting_linqOrder          sortedDescending / sortedBy / sortedByDescending -> LINQ Order/OrderBy/OrderByDescending
//
// Method bodies are self-contained; there are no shared top-level declarations, so nothing here needs the M5 prefix
// beyond the fixture class name.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

class SequenceOperationTests {
    // il-seq: lazy map/filter/take chains, materialized/short-circuited by terminals.
    @TestAttribute
    fun lazyChain() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        val r = xs.asSequence().map { it * 2 }.filter { it % 3 == 0 }.toList()
        assertEquals("6,12", r.joinToString(","))                                        // 6,12
        assertEquals(16, xs.asSequence().map { it * it }.filter { it > 10 }.first())     // 16 (first short-circuits)
        assertEquals(3, xs.asSequence().filter { it % 2 == 0 }.count())                  // 3
        assertEquals(27, xs.asSequence().map { it + 1 }.sum())                           // 27 (2+3+4+5+6+7)
        assertEquals("10-20-30", xs.asSequence().map { it * 10 }.take(3).toList().joinToString("-")) // 10-20-30
        assertEquals("1,2,3", xs.asSequence().takeWhile { it < 4 }.toList().joinToString(","))       // 1,2,3
        assertEquals("4,5,6", xs.asSequence().dropWhile { it < 4 }.toList().joinToString(","))       // 4,5,6
        assertEquals(3, xs.asSequence().single { it == 3 })                              // 3
    }

    // il-seqfilter: value-type (Int) FilteringSequence — the erased nextItem:T? box round-trip (bundle-6 BUG-1).
    @TestAttribute
    fun valueTypeFilter() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        assertEquals("3,4,5,6", xs.asSequence().filter { it > 2 }.toList().joinToString(","))                  // 3,4,5,6
        assertEquals("20,40,60", xs.asSequence().filter { it % 2 == 0 }.map { it * 10 }.toList().joinToString(",")) // 20,40,60
        assertEquals(4, xs.asSequence().filter { it > 3 }.first())                       // 4
        assertEquals("3,4,5,6", xs.asSequence().filterNot { it < 3 }.toList().joinToString(",")) // 3,4,5,6
        assertEquals(3, xs.asSequence().filter { it % 2 == 1 }.count())                  // 3
    }

    // il-sort: sortedDescending / sortedBy / sortedByDescending -> LINQ ordering, materialized by joinToString.
    @TestAttribute
    fun linqOrder() {
        val ns = listOf(3, 1, 4, 1, 5, 9, 2, 6)
        assertEquals("9,6,5,4,3,2,1,1", ns.sortedDescending().joinToString(","))         // 9,6,5,4,3,2,1,1
        val ws = listOf("bbb", "a", "cccc", "dd")
        assertEquals("a,dd,bbb,cccc", ws.sortedBy { it.length }.joinToString(","))       // a,dd,bbb,cccc
        assertEquals("cccc,bbb,dd,a", ws.sortedByDescending { it.length }.joinToString(",")) // cccc,bbb,dd,a
    }
}
