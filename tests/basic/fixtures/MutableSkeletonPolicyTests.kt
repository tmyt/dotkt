import NUnit.Framework.TestAttribute

private class PolicyCollection<T>(val values: MutableList<T>) : AbstractMutableCollection<T>() {
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = values.add(element)
    override fun iterator(): MutableIterator<T> = values.listIterator()
}

private class PolicySet<T>(val values: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = if (element in values) false else values.add(element)
    override fun iterator(): MutableIterator<T> = values.listIterator()
}

private class PolicyList<T>(val values: MutableList<T>) : AbstractMutableList<T>() {
    var ranges = 0
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) { values.add(index, element); modCount++ }
    override fun removeAt(index: Int): T {
        val previous = values.removeAt(index)
        modCount++
        return previous
    }
    override fun removeRange(fromIndex: Int, toIndex: Int) {
        ranges++
        super.removeRange(fromIndex, toIndex)
    }
}

private fun policyConcurrent(block: () -> Unit) {
    var caught = false
    try { block() } catch (_: ConcurrentModificationException) { caught = true }
    check(caught)
}

private fun <T> policyBulk(first: T, last: T) {
    val collection = PolicyCollection(mutableListOf(first, last, first))
    check(collection.removeAll(listOf(last)) && collection.values == listOf(first, first))
    check(!collection.removeAll(listOf(last)))
    check(!collection.retainAll(listOf(first)))
    check(collection.retainAll(emptyList()) && collection.isEmpty())
    val set = PolicySet(mutableListOf(first, last))
    check(set.removeAll(listOf(last)) && set.values == listOf(first))
    check(!set.removeAll(listOf(last)) && !set.retainAll(listOf(first)))
    check(set.retainAll(emptyList()) && set.isEmpty())
}

class MutableSkeletonPolicyTests {
    @TestAttribute fun collectionAndSetBulkUseSharedIteratorPolicy() {
        policyBulk(1, 2)
        policyBulk("first", "last")
        policyBulk<String?>(null, "last")
    }

    @TestAttribute fun listBulkSearchAndOrderUseUpstreamPolicy() {
        val list = PolicyList(mutableListOf<String?>(null, "a", "b", "a", null))
        check(list.indexOf(null) == 0 && list.lastIndexOf(null) == 4)
        check(list.indexOf("a") == 1 && list.lastIndexOf("a") == 3)
        check(list.indexOf("missing") == -1 && list.lastIndexOf("missing") == -1)
        check(list.removeAll(listOf("a")) && list.values == listOf(null, "b", null))
        check(!list.removeAll(listOf("a")))
        check(list.retainAll(listOf(null)) && list.values == listOf(null, null))
        check(!list.retainAll(listOf(null)))
        check(list.retainAll(emptyList()) && list.isEmpty())
    }

    @TestAttribute fun iteratorDetectsStructuralButNotElementChanges() {
        val list = PolicyList(mutableListOf(1, 2, 3))
        val cursor = list.iterator()
        list[0] = 42
        check(cursor.next() == 42)
        list.add(4)
        policyConcurrent { cursor.next() }
        policyConcurrent { cursor.remove() }
    }

    @TestAttribute fun listIteratorChecksPreviousAddAndSet() {
        val list = PolicyList(mutableListOf(1, 2, 3))
        val cursor = list.listIterator(2)
        check(cursor.previous() == 2)
        list.removeAt(0)
        policyConcurrent { cursor.previous() }
        policyConcurrent { cursor.add(4) }
        policyConcurrent { cursor.set(5) }
    }

    @TestAttribute fun iteratorOwnMutationsRefreshExpectedCount() {
        val list = PolicyList(mutableListOf<String?>("a", null, "b"))
        val cursor = list.listIterator()
        check(cursor.next() == "a")
        cursor.remove()
        check(cursor.next() == null)
        cursor.set("changed")
        cursor.add(null)
        check(cursor.previous() == null && cursor.previous() == "changed")
        check(list.values == listOf("changed", null, "b"))
    }

    @TestAttribute fun subListRejectsStaleReadsWritesAndNewIterators() {
        val list = PolicyList(mutableListOf(1, 2, 3, 4))
        val view = list.subList(1, 3)
        list.add(5)
        policyConcurrent { view.size }
        policyConcurrent { view[0] }
        policyConcurrent { view[0] = 9 }
        policyConcurrent { view.add(0, 9) }
        policyConcurrent { view.removeAt(0) }
        policyConcurrent { view.clear() }
        policyConcurrent { view.iterator() }
        policyConcurrent { view.listIterator(0) }
    }

    @TestAttribute fun subListOwnChangesAndRangeRemovalStayLive() {
        val list = PolicyList(mutableListOf(0, 1, 2, 3, 4))
        val view = list.subList(1, 4)
        val cursor = view.listIterator()
        check(cursor.next() == 1)
        cursor.remove()
        cursor.add(8)
        check(cursor.next() == 2)
        cursor.set(9)
        check(view.toList() == listOf(8, 9, 3) && list.values == listOf(0, 8, 9, 3, 4))
        view.clear()
        check(list.ranges == 1 && view.isEmpty() && list.values == listOf(0, 4))
        view.add(7)
        check(view.single() == 7 && list.values == listOf(0, 7, 4))
    }

    @TestAttribute fun nestedSubListsPropagateChangesAndInvalidateSiblings() {
        val list = PolicyList(mutableListOf(0, 1, 2, 3, 4, 5))
        val view = list.subList(1, 5)
        val nested = view.subList(1, 3)
        val sibling = view.subList(0, 1)
        nested.clear()
        check(list.ranges == 1 && nested.isEmpty())
        check(view.toList() == listOf(1, 4) && list.values == listOf(0, 1, 4, 5))
        policyConcurrent { sibling.size }
        nested.add(7)
        check(nested.single() == 7)
        list.add(6)
        policyConcurrent { nested[0] }
    }
}
