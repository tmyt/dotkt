import NUnit.Framework.TestAttribute

private fun <T> portableCollectionIndices(values: Collection<T>): IntRange = values.indices
private fun <T> portableListFill(values: MutableList<T>, value: T) { values.fill(value) }

private class PortableHelperKey(val value: Int) {
    override fun equals(other: Any?): Boolean = other is PortableHelperKey && value == other.value
    override fun hashCode(): Int = value
}

private class PortableHelperGrouping<T, K>(val values: List<T>, val classify: (T) -> K) : Grouping<T, K> {
    var starts = 0
    var visits = 0
    override fun sourceIterator(): Iterator<T> { starts++; return values.iterator() }
    override fun keyOf(element: T): K { visits++; return classify(element) }
}

private class PortableHelperIterable<T>(val values: List<T>) : Iterable<T> {
    var starts = 0
    var visits = 0
    override fun iterator(): Iterator<T> {
        starts++
        val underlying = values.iterator()
        return object : Iterator<T> {
            override fun hasNext(): Boolean = underlying.hasNext()
            override fun next(): T { visits++; return underlying.next() }
        }
    }
}

private class PortableHelperList<T>(val values: MutableList<T>) : AbstractMutableList<T>() {
    var writes = 0
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T { writes++; return values.set(index, element) }
    override fun add(index: Int, element: T) { values.add(index, element) }
    override fun removeAt(index: Int): T = values.removeAt(index)
}

class PortableCollectionHelperTests {
    @TestAttribute fun collectionIndicesWorkThroughStarGenericAndNativeViews() {
        val collections = listOf<Collection<*>>(
            emptyList<Int>(), listOf(3, 2, 1), listOf("a", null),
            setOf(PortableHelperKey(1), PortableHelperKey(2)),
            listOf(System.TimeSpan(7L)))
        for (values in collections) check(values.indices.toList() == (0 until values.size).toList())
        check(portableCollectionIndices(listOf(1, 2, 3)) == 0..2)
        check(portableCollectionIndices(listOf("a", null)) == 0..1)
        val native = System.Collections.Generic.List<Int>()
        native.Add(7); native.Add(8)
        val star = native as Collection<*>
        check(star.indices == 0..1)
        native.Add(9)
        check(star.indices == 0..2)
        native.Clear()
        check(star.indices.isEmpty())
    }

    @TestAttribute fun groupingCountsPreserveNullKeysEqualityAndEncounterOrder() {
        val values = listOf(2, 1, 2, 0, 1, 0)
        val source = PortableHelperGrouping(values) { n: Int -> if (n == 0) null else PortableHelperKey(n) }
        val result = source.eachCount()
        check(source.starts == 1 && source.visits == values.size)
        check(result.keys.map { it?.value } == listOf(2, 1, null))
        check(result[PortableHelperKey(2)] == 2 && result[PortableHelperKey(1)] == 2 && result[null] == 2)
        check(result.values.toList() == listOf(2, 2, 2))
        check(source.eachCount() == result)
        check(source.starts == 2 && source.visits == 2 * values.size)
        val empty = PortableHelperGrouping(emptyList<Int>()) { it }
        check(empty.eachCount().isEmpty() && empty.starts == 1 && empty.visits == 0)
        check(listOf("a", null, "a", null).groupingBy { it }.eachCount() == mapOf("a" to 2, null to 2))
    }

    @TestAttribute fun fillUsesMutableListSlotsAndRetainsNativeStorage() {
        val values = PortableHelperList(mutableListOf<String?>("a", null, "b"))
        portableListFill(values, null)
        check(values.values == listOf(null, null, null) && values.writes == 3)
        val fill: (String?) -> Unit = values::fill
        fill("filled")
        check(values.values == listOf("filled", "filled", "filled") && values.writes == 6)
        val empty = PortableHelperList(mutableListOf<Int>())
        empty.fill(9)
        check(empty.writes == 0 && empty.isEmpty())
        val native = System.Collections.Generic.List<Int>()
        native.Add(1); native.Add(2)
        val view = native as MutableList<Int>
        portableListFill(view, 42)
        check((view as Any) === native && native.ToArray().toList() == listOf(42, 42))
    }

    @TestAttribute fun shuffleWritesTheOriginalListAndKeepsEveryElement() {
        val input = List(32) { it % 5 }
        val values = PortableHelperList(input.toMutableList())
        val shuffle: () -> Unit = values::shuffle
        shuffle()
        check(values.writes == 2 * (input.size - 1))
        check(values.values.sorted() == input.sorted())
        values.shuffle()
        check(values.writes == 4 * (input.size - 1) && values.values.sorted() == input.sorted())
        val empty = PortableHelperList(mutableListOf<Int>())
        empty.shuffle(); check(empty.writes == 0)
        val single = PortableHelperList(mutableListOf("only"))
        single.shuffle(); check(single.writes == 0 && single[0] == "only")
        val references = mutableListOf<Any?>(PortableHelperKey(7), null, PortableHelperKey(8), null)
        val before = references.toList()
        references.shuffle()
        for (value in before) check(references.count { it === value } == before.count { it === value })
    }

    @TestAttribute fun shuffledEnumeratesOnceAndReturnsAnIndependentCopy() {
        val values = listOf(3, 1, 2, 3, 0)
        val source = PortableHelperIterable(values)
        val result = source.shuffled()
        check(source.starts == 1 && source.visits == values.size)
        check(result.sorted() == values.sorted() && values == listOf(3, 1, 2, 3, 0))
        val mutable = mutableListOf(1, 2, 3)
        val copied = mutable.shuffled()
        check((copied as Any) !== mutable && copied.sorted() == listOf(1, 2, 3))
        mutable.clear()
        check(copied.sorted() == listOf(1, 2, 3))
        val empty = PortableHelperIterable(emptyList<Int>())
        check(empty.shuffled().isEmpty() && empty.starts == 1 && empty.visits == 0)
    }
}
