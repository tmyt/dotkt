// Lazy-sequence + sorting battery. Migrates the Sequence/LINQ family of cases/il-* onto the in-process
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
// valueAccumulatorSingle is not migrated from a case: it is the #86 value-instantiation armor for the
// `T? = null` accumulator behind Sequence.single/singleOrNull (see the comment on the method).
//
// Method bodies are self-contained; there are no shared top-level declarations.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

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

    // #284: array-backed sequences used to reach Array<T>.iterator() through an open generic implementation type
    // whose captured T was not owned by the emitted type. At T=Int that mismatched the actual Iterator<Int> and the
    // process died with AccessViolationException. Force both public entry points all the way through iteration and
    // materialization; a merely constructible Sequence is not sufficient evidence for this failure mode.
    @TestAttribute
    fun arrayBackedSequenceIteration() {
        assertEquals("1,2,3", sequenceOf(1, 2, 3).toList().joinToString(","))
        assertEquals("4,5,6", arrayOf(4, 5, 6).asSequence().toList().joinToString(","))
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

    // #86 — `Sequence.single{}` keeps a `var single: T? = null` ACCUMULATOR local, the exact shape whose erased
    // object slot has to survive a real null and unbox at the `single as T` read; at T=Int/T=Boolean a bare `T`
    // slot cannot distinguish "not seen yet" from the value 0/false. Receivers remain List.asSequence() throughout
    // so this method measures the accumulator seam alone; array-backed sequence iteration is pinned separately above.
    @TestAttribute
    fun valueAccumulatorSingle() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        assertEquals(1, xs.asSequence().filter { it < 2 }.single())               // 1  (predicate-less single)
        assertEquals(6, xs.asSequence().singleOrNull { it > 5 })                  // 6
        assertNull(xs.asSequence().singleOrNull { it > 10 })                      // null (accumulator never set)
        val bs = listOf(true, false, false)
        assertTrue(bs.asSequence().single { it })                                 // true
        assertFalse(bs.asSequence().filter { !it }.first())                       // false
        assertEquals(2, bs.asSequence().filter { !it }.count())                   // 2
    }

    @TestAttribute
    fun mapNotNullAtValueElements() {
        var calls = 0
        val ints = sequenceOf(1, 2, 3, 4).mapNotNull {
            calls = calls + 1
            if (it % 2 == 1) it * 10 else null
        }
        assertEquals(0, calls)
        assertEquals("10,30", ints.toList().joinToString(","))
        assertEquals(4, calls)

        val bools = sequenceOf(0, 1, 2).mapNotNull { if (it == 1) null else it == 2 }
        assertEquals("False,True", bools.toList().joinToString(","))

        val direct = sequenceOf(1, 2, 3).map { if (it == 2) null else it }.filterNotNull()
        assertEquals("1,3", direct.toList().joinToString(","))

        val stringSource = ArrayList<String>()
        stringSource.add("a")
        stringSource.add("bb")
        stringSource.add("c")
        val strings = stringSource.asSequence().mapNotNull { if (it.length == 1) it else null }
        assertEquals("a,c", strings.toList().joinToString(","))

        val indexed = sequenceOf(10, 20, 30).mapIndexedNotNull { index, value ->
            if (index == 1) null else value + index
        }
        assertEquals("10,32", indexed.toList().joinToString(","))
    }

    @TestAttribute
    fun filterIsInstanceAtTypedElements() {
        val source = ArrayList<Any?>()
        source.add(1)
        source.add("a")
        source.add(null)
        source.add(2)
        source.add("bb")

        var calls = 0
        val ints = source.asSequence().map { calls = calls + 1; it }.filterIsInstance<Int>()
        assertEquals(0, calls)
        assertEquals("1,2", ints.toList().joinToString(","))
        assertEquals(5, calls)
        assertEquals("a,bb", source.asSequence().filterIsInstance<String>().toList().joinToString(","))
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
