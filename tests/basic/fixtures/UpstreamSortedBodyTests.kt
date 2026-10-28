import NUnit.Framework.TestAttribute

private enum class SortPhase { FIRST, SECOND, THIRD }

private class SortToken<T>(val priority: Int, val label: T) : Comparable<SortToken<T>> {
    override fun compareTo(other: SortToken<T>): Int = priority.compareTo(other.priority)
}

private class SortInput<T>(private val values: List<T>) : AbstractCollection<T>() {
    var iterations = 0
    var reads = 0
    override val size: Int get() = values.size
    override fun iterator(): Iterator<T> {
        iterations++
        val underlying = values.iterator()
        return object : Iterator<T> {
            override fun hasNext(): Boolean = underlying.hasNext()
            override fun next(): T { reads++; return underlying.next() }
        }
    }
}

private fun <T> onlyIterable(values: List<T>): Iterable<T> = object : Iterable<T> {
    override fun iterator(): Iterator<T> = values.iterator()
}

class UpstreamSortedBodyTests {
    @TestAttribute
    fun collectionAndIterableSortsKeepStableValuesAndInputs() {
        val a = SortToken(2, "a")
        val b = SortToken(1, "b")
        val c = SortToken(2, "c")
        val input = SortInput(listOf(a, b, c))
        val natural = input.sorted()
        check(natural[0] === b && natural[1] === a && natural[2] === c)
        check(input.iterations == 1 && input.reads == 3)
        check(input.toList() == listOf(a, b, c))
        val comparator = Comparator<SortToken<String>> { x, y -> x.priority.compareTo(y.priority) }
        val explicit = input.sortedWith(comparator)
        check(explicit[0] === b && explicit[1] === a && explicit[2] === c)
        check(onlyIterable(listOf(a, b, c)).sorted() == natural)
        check(onlyIterable(listOf(a, b, c)).sortedWith(comparator) == natural)
        check(listOf(3, 1, 2).sorted() == listOf(1, 2, 3))
        check(listOf("c", "a", "b").sorted() == listOf("a", "b", "c"))
        check(listOf(SortPhase.THIRD, SortPhase.FIRST, SortPhase.SECOND).sorted() ==
            listOf(SortPhase.FIRST, SortPhase.SECOND, SortPhase.THIRD))
        check(listOf<Int?>(2, null, 1).sortedWith(Comparator { x, y -> compareValues(x, y) }) == listOf(null, 1, 2))
        val durations = listOf(System.TimeSpan(0, 0, 3), System.TimeSpan(0, 0, 1))
        val ordered = durations.sortedWith(Comparator { x, y -> x.TotalSeconds.compareTo(y.TotalSeconds) })
        check(ordered[0].TotalSeconds == 1.0 && durations[0].TotalSeconds == 3.0)
    }

    @TestAttribute
    fun smallCollectionsAvoidComparisonsAndFailuresPreserveInputs() {
        var comparisons = 0
        val unused = Comparator<Int> { x, y -> comparisons++; x.compareTo(y) }
        check(emptyList<Int>().sorted().isEmpty())
        check(emptyList<Int>().sortedWith(unused).isEmpty())
        check(listOf(7).sorted() == listOf(7))
        check(listOf(7).sortedWith(unused) == listOf(7))
        check(comparisons == 0)
        val failure = IllegalStateException("comparison witness")
        val source = mutableListOf(3, 1, 2)
        var caught = false
        try { source.sortedWith(Comparator { _, _ -> throw failure }) }
        catch (error: IllegalStateException) { check(error === failure); caught = true }
        check(caught && source == listOf(3, 1, 2))
    }
}
