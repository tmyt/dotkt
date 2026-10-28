package roundtrip.consumer

import NUnit.Framework.TestAttribute
import roundtrip.sortedbodies.natural
import roundtrip.sortedbodies.ordered
import roundtrip.sortedbodies.orderedInline

class ImportedSortedBodyTests {
    @TestAttribute
    fun producerBodiesAndImportedInlineCallsSortValuesStably() {
        check(natural(listOf(3, 1, 2)) == listOf(1, 2, 3))
        check(natural(listOf("c", "a", "b")) == listOf("a", "b", "c"))
        val nullable = listOf<Int?>(2, null, 1)
        check(ordered(nullable, Comparator { a, b -> compareValues(a, b) }) == listOf(null, 1, 2))
        check(orderedInline(nullable) { a, b -> compareValues(a, b) } == listOf(null, 1, 2))
        val first = Pair(2, "first")
        val middle = Pair(1, "middle")
        val last = Pair(2, "last")
        val values = listOf(first, middle, last)
        val sorted = orderedInline(values) { a, b -> a.first.compareTo(b.first) }
        check(sorted[0] === middle && sorted[1] === first && sorted[2] === last)
        check(values[0] === first && values[2] === last)
        val spans = listOf(System.TimeSpan(0, 0, 3), System.TimeSpan(0, 0, 1))
        check(orderedInline(spans) { a, b -> a.TotalSeconds.compareTo(b.TotalSeconds) }[0].TotalSeconds == 1.0)
        val iterable = object : Iterable<Int> {
            override fun iterator(): Iterator<Int> = listOf(3, 1, 2).iterator()
        }
        check(natural(iterable) == listOf(1, 2, 3))
    }
}
