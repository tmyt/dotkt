@file:Suppress("UNCHECKED_CAST")

import NUnit.Framework.TestAttribute

private fun <S, T> sequenceView(source: Sequence<S>): Sequence<T> = source as Sequence<T>

private inline fun <reified T> selectSequence(source: Sequence<*>): Sequence<T> =
    source.filter { it is T } as Sequence<T>

private fun <T : Any> nonNullSequence(source: Sequence<T?>): Sequence<T> =
    source.filterNot { it == null } as Sequence<T>

class SequenceElementViewTests {
    @TestAttribute
    fun uncheckedViewsPreserveIdentityAndReadElementsLazily() {
        var reads = 0
        val source: Sequence<Any?> = listOf<Any?>(null, "skip", 7, 9, "tail").asSequence()
            .map { reads++; it }
        val selected = selectSequence<Int>(source)
        check(reads == 0)
        check(selected.take(1).single() == 7 && reads == 3)
        check(selected.toList() == listOf(7, 9) && reads == 8)
        check(source.filterIsInstance<Int>().toList() == listOf(7, 9) && reads == 13)
        check(selectSequence<String>(source).toList() == listOf("skip", "tail"))
        check(selectSequence<String?>(source).toList() == listOf(null, "skip", "tail"))
        check(source.filterIsInstance<Int?>().toList() == listOf(null, 7, 9))
        var sum = 0
        for (value in source.filterIsInstance<Int>()) sum += value
        check(sum == 16)

        val numbers = sequenceOf(1, 2)
        val widened: Sequence<Any> = numbers
        val view = sequenceView<Any, Int>(widened)
        check(view === numbers && widened === numbers)
        check(view.iterator().next() == 1 && view.toList() == listOf(1, 2))

        val bad = sequenceView<String, Int>(sequenceOf("not an integer"))
        var failed = false
        try { val n: Int = bad.first(); check(n == 0) } catch (_: ClassCastException) { failed = true }
        check(failed)
    }

    @TestAttribute
    fun nonNullViewsHandleNativeValuesReferencesAndSingleUseSources() {
        var reads = 0
        val source = sequenceOf<Int?>(null, 3, null, 5).map { reads++; it }
        val selected = nonNullSequence(source)
        check(reads == 0)
        check(selected.take(1).single() == 3 && reads == 2)
        check(selected.toList() == listOf(3, 5) && reads == 6)
        check(source.filterNotNull().toList() == listOf(3, 5) && reads == 10)
        check(sequenceOf<String?>(null, "a", null, "b").filterNotNull().toList() == listOf("a", "b"))
        val duration = System.TimeSpan(0, 0, 7)
        val durations: Sequence<System.TimeSpan?> = sequenceOf(null, duration, null)
        check(nonNullSequence(durations).single() == duration)
        check(durations.filterNotNull().single() == duration)
        check(selectSequence<System.TimeSpan>(sequenceOf<Any?>(null, "other", duration)).single() == duration)
        var seconds = 0.0
        for (value in sequenceOf<Any?>(null, duration).filterIsInstance<System.TimeSpan>()) seconds += value.TotalSeconds
        check(seconds == 7.0)

        val token = Any()
        check(nonNullSequence(sequenceOf(null, token)).single() === token)
        val once = source.constrainOnce().filterNotNull()
        check(once.toList() == listOf(3, 5))
        var failed = false
        try { once.iterator() } catch (_: IllegalStateException) { failed = true }
        check(failed)
    }
}
