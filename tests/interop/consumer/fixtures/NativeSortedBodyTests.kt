import NUnit.Framework.TestAttribute
import SortedBodies.NativeSortInputs

class NativeSortedBodyTests {
    @TestAttribute
    fun nativeGenericOnlyCollectionsUseUpstreamSortingBodies() {
        val numbers = NativeSortInputs.Integers() as Collection<Int>
        check(numbers.sorted() == listOf(1, 2, 3))
        check(numbers.toList() == listOf(3, 1, 2))
        val strings = NativeSortInputs.Strings() as Collection<String>
        check(strings.sorted() == listOf("a", "b", "c"))
        val durations = NativeSortInputs.Durations() as Collection<System.TimeSpan>
        val sorted = durations.sortedWith(Comparator { a, b -> a.TotalSeconds.compareTo(b.TotalSeconds) })
        check(sorted[0].TotalSeconds == 1.0 && durations.first().TotalSeconds == 3.0)
    }
}
