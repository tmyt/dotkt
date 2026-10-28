package roundtrip.consumer

import NUnit.Framework.TestAttribute
import roundtrip.sequenceviews.nonNull
import roundtrip.sequenceviews.integers
import roundtrip.sequenceviews.selected
import roundtrip.sequenceviews.selectedInline
import roundtrip.sequenceviews.view

class ImportedSequenceElementViewTests {
    @TestAttribute
    fun plainAndReifiedInlineProducerViewsKeepSourceSemantics() {
        var reads = 0
        val source = sequenceOf<Any?>(null, "a", 7, "b", 9).map { reads++; it }
        val values = integers(source)
        check(reads == 0 && values.take(1).single() == 7 && reads == 3)
        check(values.toList() == listOf(7, 9) && reads == 8)
        check(selected<String>(source).toList() == listOf("a", "b"))
        check(selected<String?>(source).toList() == listOf(null, "a", "b"))
        var predicates = 0
        val expanded = selectedInline<Int>(source) { predicates++; it > 7 }
        check(predicates == 0 && expanded.single() == 9 && predicates == 2)
        check(selectedInline<String?>(source) { it == null || it.length == 1 }.toList() == listOf(null, "a", "b"))
        check(nonNull(sequenceOf<Int?>(null, 3, null, 5)).toList() == listOf(3, 5))
        val duration = System.TimeSpan(0, 0, 11)
        check(nonNull(sequenceOf<System.TimeSpan?>(null, duration)).single() == duration)
        check(selected<System.TimeSpan>(sequenceOf<Any?>(null, duration)).single() == duration)
        check(selectedInline<System.TimeSpan>(sequenceOf<Any?>(null, duration)) { it.TotalSeconds == 11.0 }.single() == duration)
        val original = sequenceOf(1, 2)
        val widened: Sequence<Any> = original
        val narrowed = view<Any, Int>(widened)
        check(narrowed === original && narrowed.iterator().next() == 1)
    }
}
