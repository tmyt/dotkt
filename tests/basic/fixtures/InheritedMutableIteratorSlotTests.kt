import NUnit.Framework.TestAttribute

private class InheritedIteratorCollection<T>(private val items: MutableList<T>) : AbstractMutableCollection<T>() {
    var iteratorCalls = 0
    override val size: Int get() = items.size
    override fun add(element: T): Boolean = items.add(element)
    override fun iterator(): MutableIterator<T> { iteratorCalls++; return items.iterator() }
}

private fun <T> verifyInheritedIteratorOperations(first: T, second: T, absent: T) {
    val items = mutableListOf(first, second, first)
    val collection = InheritedIteratorCollection(items)
    check(collection.contains(first) && !collection.contains(absent))
    check(collection.containsAll(listOf(first, second)))
    check(collection.remove(first) && items.size == 2 && items[0] == second)
    check(!collection.remove(absent))
    check(collection.removeAll(listOf(second)) && items.size == 1 && items[0] == first)
    collection.add(second)
    collection.add(absent)
    check(collection.retainAll(listOf(first)) && items.size == 1 && items[0] == first)
    collection.clear()
    check(collection.isEmpty() && items.isEmpty() && collection.iteratorCalls > 0)
}

class InheritedMutableIteratorSlotTests {
    @TestAttribute fun inheritedContainsAndRemoveUseTheAuthoredMutableIterator() {
        val items = mutableListOf("value", "other")
        val authored = InheritedIteratorCollection(items)
        val collection: MutableCollection<String> = authored
        check(collection.contains("value") && !collection.contains("absent"))
        check(collection.remove("value") && items.size == 1 && items[0] == "other")
        check(authored.iteratorCalls > 0)
    }

    @TestAttribute fun inheritedOperationsRetainGenericValueAndNullableFrames() {
        verifyInheritedIteratorOperations("first", "second", "absent")
        verifyInheritedIteratorOperations(7, 9, 11)
        verifyInheritedIteratorOperations<Int?>(null, 7, 9)
        verifyInheritedIteratorOperations<String?>(null, "value", "absent")
    }

    @TestAttribute fun concreteAndNativeIteratorPathsRemainValid() {
        val set = linkedSetOf(7, 9)
        val mutable = set.iterator()
        check(mutable.hasNext() && mutable.next() == 7)
        mutable.remove()
        check(set.size == 1 && set.contains(9))
        val native = listOf("first", "second").iterator()
        check(native.hasNext() && native.next() == "first")
        check(native.hasNext() && native.next() == "second" && !native.hasNext())
    }
}
