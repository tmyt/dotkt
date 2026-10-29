package roundtriptests.portablecollections

import NUnit.Framework.TestAttribute
import roundtrip.portablecollections.*

class PortableCollectionHelperTests {
    @TestAttribute fun importedHelpersPreserveStarReceiversAndNullableGroupKeys() {
        check(collectionIndices(listOf(1, 2, 3)) == 0..2)
        check(collectionIndices(listOf("a", null)) == 0..1)
        check(collectionIndices(emptySet<Int>()).isEmpty())
        val native = System.Collections.Generic.List<Int>()
        native.Add(1); native.Add(2)
        check(collectionIndices(native as Collection<*>) == 0..1)
        val counted = groupedCounts(listOf("first", null, "first", "last", null)) { it }
        check(counted.keys.toList() == listOf("first", null, "last"))
        check(counted["first"] == 2 && counted[null] == 2 && counted["last"] == 1)
        check(groupedCounts(listOf(1, 2, 3, 4)) { it % 2 } == mapOf(1 to 2, 0 to 2))
    }

    @TestAttribute fun importedListBodiesMutateOrCopyAtTheDeclaredBoundary() {
        val values = mutableListOf(1, 2, 3)
        fillList(values, 7)
        check(values == listOf(7, 7, 7))
        val fill = savedFill(values)
        fill(11)
        check(values == listOf(11, 11, 11))
        val nullable = mutableListOf<String?>("a", "b")
        fillList(nullable, null)
        check(nullable == listOf(null, null))
        val input = mutableListOf(3, 2, 3, 1)
        val before = input.toList()
        shuffleList(input)
        check(input.sorted() == before.sorted())
        val shuffle = savedShuffle(input)
        shuffle()
        check(input.sorted() == before.sorted())
        val copy = shuffledList(input)
        check((copy as Any) !== input && copy.sorted() == before.sorted())
        input.clear()
        check(copy.sorted() == before.sorted())
    }
}
