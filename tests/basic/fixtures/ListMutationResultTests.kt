import NUnit.Framework.TestAttribute

private open class ListMutationResults<T>(
    private val backing: MutableList<T>,
    private val setResult: T,
    private val removeResult: T,
) : MutableList<T> by backing {
    var gets = 0
    var sets = 0
    var removals = 0
    var rejectGet = false
    var failure: Throwable? = null
    var receiver: Any? = null
    var lastIndex = -1
    var lastElement: Any? = null

    override fun get(index: Int): T {
        gets++
        if (rejectGet) throw IllegalStateException("unexpected getter")
        return backing[index]
    }
    override fun set(index: Int, element: T): T {
        sets++
        receiver = this
        lastIndex = index
        lastElement = element
        failure?.let { throw it }
        backing[index] = element
        return setResult
    }
    override fun removeAt(index: Int): T {
        removals++
        receiver = this
        lastIndex = index
        failure?.let { throw it }
        backing.removeAt(index)
        return removeResult
    }
}

private class ListMutationDerived : ListMutationResults<String>(mutableListOf("old"), "base-set", "base-remove") {
    override fun set(index: Int, element: String): String {
        super.set(index, element)
        return "derived-set"
    }
    override fun removeAt(index: Int): String {
        super.removeAt(index)
        return "derived-remove"
    }
}

private class ListMutationAbstractBase : AbstractMutableList<Int>() {
    private val backing = mutableListOf(5)
    var gets = 0
    override val size: Int get() = backing.size
    override fun get(index: Int): Int { gets++; return backing[index] }
    override fun add(index: Int, element: Int) { backing.add(index, element) }
    override fun set(index: Int, element: Int): Int { backing[index] = element; return 77 }
    override fun removeAt(index: Int): Int { backing.removeAt(index); return 88 }
}

private class ListMutationUnitResults(private val backing: MutableList<Unit> = mutableListOf(Unit)) : MutableList<Unit> by backing {
    var gets = 0
    override fun get(index: Int) { gets++; backing[index] }
    override fun set(index: Int, element: Unit) { backing[index] = element }
    override fun removeAt(index: Int) { backing.removeAt(index) }
}

private open class ListMutationSelectedBody<T>(
    protected val items: MutableList<T>, private val setResult: T, private val removeResult: T,
) {
    fun set(index: Int, element: T): T { items[index] = element; return setResult }
    fun removeAt(index: Int): T { items.removeAt(index); return removeResult }
    fun addAll(index: Int, elements: Collection<T>): Boolean = items.addAll(index, elements)
}

private class ListMutationSelectedResults<T>(private val backing: MutableList<T>, setResult: T, removeResult: T) :
    ListMutationSelectedBody<T>(backing, setResult, removeResult), MutableList<T> {
    var gets = 0
    override val size: Int get() = backing.size
    override fun get(index: Int): T { gets++; return backing[index] }
    override fun isEmpty(): Boolean = backing.isEmpty()
    override fun contains(element: T): Boolean = backing.contains(element)
    override fun containsAll(elements: Collection<T>): Boolean = backing.containsAll(elements)
    override fun indexOf(element: T): Int = backing.indexOf(element)
    override fun lastIndexOf(element: T): Int = backing.lastIndexOf(element)
    override fun iterator(): MutableIterator<T> = backing.iterator()
    override fun listIterator(): MutableListIterator<T> = backing.listIterator()
    override fun listIterator(index: Int): MutableListIterator<T> = backing.listIterator(index)
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<T> = backing.subList(fromIndex, toIndex)
    override fun add(element: T): Boolean = backing.add(element)
    override fun add(index: Int, element: T) { backing.add(index, element) }
    override fun addAll(elements: Collection<T>): Boolean = backing.addAll(elements)
    override fun remove(element: T): Boolean = backing.remove(element)
    override fun removeAll(elements: Collection<T>): Boolean = backing.removeAll(elements)
    override fun retainAll(elements: Collection<T>): Boolean = backing.retainAll(elements)
    override fun clear() { backing.clear() }
}

private fun <T> listMutationExactSet(list: MutableList<T>, value: T): T = list.set(0, value)
private fun <T> listMutationExactRemove(list: MutableList<T>): T = list.removeAt(0)
private fun listMutationProjectedSet(list: MutableList<in String>, value: String): Any? = list.set(0, value)
private fun listMutationProjectedRemove(list: MutableList<*>): Any? = list.removeAt(0)

class ListMutationResultTests {
    @TestAttribute fun exactAndProjectedCallsPreserveGenericResults() {
        val strings = ListMutationResults(mutableListOf("old"), "set-result", "remove-result")
        check(listMutationExactSet(strings, "new") == "set-result")
        check(listMutationExactRemove(strings) == "remove-result")
        check(strings.gets == 0 && strings.sets == 1 && strings.removals == 1)

        val projected = ListMutationResults(mutableListOf("old"), "projected-set", "projected-remove")
        check(listMutationProjectedSet(projected, "new") == "projected-set")
        check(listMutationProjectedRemove(projected) == "projected-remove")
        check(projected.gets == 0 && projected.sets == 1 && projected.removals == 1)

        val ints = ListMutationResults(mutableListOf(1), 42, 99)
        check(listMutationExactSet(ints, 2) == 42)
        check(listMutationProjectedRemove(ints) == 99)
        check(ints.gets == 0)

        val nullable = ListMutationResults<Int?>(mutableListOf(1), null, null)
        check(listMutationExactSet(nullable, null) == null)
        check(listMutationExactRemove(nullable) == null)
        check(nullable.gets == 0 && nullable.lastElement == null)
        val units = ListMutationUnitResults()
        val unitResult: Any? = listMutationExactSet(units, Unit)
        check(unitResult === Unit && listMutationProjectedRemove(units) === Unit && units.gets == 0)
    }

    @TestAttribute fun mutationsDoNotReadTheGetterAndPreserveExceptionsAndReceiver() {
        val list = ListMutationResults(mutableListOf("old"), "set-result", "remove-result")
        list.rejectGet = true
        check(listMutationProjectedSet(list, "new") == "set-result")
        check(list.receiver === list && list.lastIndex == 0 && list.lastElement == "new")
        val failure = IllegalStateException("mutation failure")
        list.failure = failure
        try { listMutationExactSet(list, "fail"); error("expected setter failure") }
        catch (caught: Throwable) { check(caught === failure) }
        try { listMutationProjectedRemove(list); error("expected removal failure") }
        catch (caught: Throwable) { check(caught === failure) }
        check(list.gets == 0 && list.sets == 2 && list.removals == 1)
        check(list.receiver === list && list.lastIndex == 0)
        list.failure = null
        check(listMutationExactRemove(list) == "remove-result")
        check(list.gets == 0 && list.removals == 2 && list.size == 0)
    }

    @TestAttribute fun inheritedOverridesAndLiveSubListsRetainMutationResults() {
        val inherited = ListMutationDerived()
        check(listMutationExactSet(inherited, "new") == "derived-set")
        check(listMutationProjectedRemove(inherited) == "derived-remove")
        check(inherited.gets == 0 && inherited.sets == 1 && inherited.removals == 1)
        val root = ListMutationAbstractBase()
        check(listMutationExactSet(root, 6) == 77)
        check(listMutationProjectedRemove(root) == 88)
        val mutableRoot: MutableList<Int> = root
        check(mutableRoot.addAll(0, listOf(5)))
        val slice = root.subList(0, 1)
        check(slice.set(0, 6) == 77)
        check(slice.removeAt(0) == 88)
        check(root.gets == 0 && root.size == 0 && slice.size == 0)
    }

    @TestAttribute fun inheritedNonCollectionBodiesSupplyMutationSlots() {
        val strings = ListMutationSelectedResults(mutableListOf("old"), "selected-set", "selected-remove")
        check(listMutationProjectedSet(strings, "new") == "selected-set")
        check(listMutationExactRemove(strings) == "selected-remove" && strings.gets == 0)
        val ints = ListMutationSelectedResults(mutableListOf(5), 77, 88)
        check(listMutationExactSet(ints, 6) == 77)
        check(listMutationProjectedRemove(ints) == 88 && ints.gets == 0)
        val nullable = ListMutationSelectedResults<Int?>(mutableListOf(5), null, null)
        check(listMutationExactSet(nullable, null) == null)
        check(listMutationProjectedRemove(nullable) == null && nullable.gets == 0)
    }

    @TestAttribute fun nativeVoidMutationsStillReturnThePreviousValue() {
        val exact = mutableListOf("old")
        check(listMutationExactSet(exact, "new") == "old")
        check(listMutationExactRemove(exact) == "new")
        check(exact.isEmpty())
        val projected = mutableListOf("old")
        check(listMutationProjectedSet(projected, "new") == "old")
        check(listMutationProjectedRemove(projected) == "new")
        check(projected.isEmpty())
        val nullable = mutableListOf<Int?>(null)
        check(listMutationExactSet(nullable, 7) == null)
        check(listMutationExactRemove(nullable) == 7)
    }
}
