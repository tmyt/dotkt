import NUnit.Framework.TestAttribute

private open class CollectionAddResultList<T>(
    private val storage: MutableList<T>,
    private val accepted: Boolean = true,
) : MutableList<T> by storage {
    var calls = 0
    var failure: IllegalStateException? = null
    override fun add(element: T): Boolean {
        calls++
        val thrown = failure
        if (thrown != null) throw thrown
        if (accepted) storage[0] = element
        return accepted
    }
}

private class CollectionAddResultInherited<T>(storage: MutableList<T>) : CollectionAddResultList<T>(storage)
private class CollectionAddResultNativeList : System.Collections.Generic.List<Int>()

private class CollectionAddResultDerived<T>(private val storage: MutableList<T>) : CollectionAddResultList<T>(storage) {
    override fun add(element: T): Boolean { calls++; storage[0] = element; return false }
}

private interface CollectionAddResultDefault : MutableCollection<Int> {
    val values: MutableList<Int>
    override fun add(element: Int): Boolean { values[0] = element; return true }
}

private class CollectionAddResultDefaultCollection : CollectionAddResultDefault {
    override val values: MutableList<Int> = mutableListOf(1)
    override val size: Int get() = values.size
    override fun isEmpty() = values.isEmpty()
    override fun contains(element: Int) = values.contains(element)
    override fun containsAll(elements: Collection<Int>) = values.containsAll(elements)
    override fun iterator() = values.iterator()
    override fun addAll(elements: Collection<Int>) = values.addAll(elements)
    override fun remove(element: Int) = values.remove(element)
    override fun removeAll(elements: Collection<Int>) = values.removeAll(elements)
    override fun retainAll(elements: Collection<Int>) = values.retainAll(elements)
    override fun clear() = values.clear()
}

private fun <T> collectionAddResultGeneric(receiver: MutableCollection<T>, element: T): Boolean = receiver.add(element)

private class RejectingSet<E>(items: MutableSet<E>) : MutableSet<E> by items {
    override fun add(element: E): Nothing = throw UnsupportedOperationException("add")
    override fun addAll(elements: Collection<E>): Nothing = throw UnsupportedOperationException("addAll")
}

private abstract class PartialSet<E> : MutableSet<E> {
    override val size get() = 0
    override fun isEmpty() = true
    override fun clear() {}
    override fun contains(element: E) = false
    override fun containsAll(elements: Collection<E>) = elements.isEmpty()
    override fun iterator() = mutableSetOf<E>().iterator()
    override fun remove(element: E) = false
    override fun removeAll(elements: Collection<E>) = false
    override fun retainAll(elements: Collection<E>) = false
}
private class ReadOnlyStringSet : PartialSet<String>() {
    override fun add(element: String): Nothing = throw UnsupportedOperationException(element)
    override fun addAll(elements: Collection<String>): Nothing = throw UnsupportedOperationException("all")
}
private abstract class BaseIntSlot { abstract fun read(): Int }
private class ThrowingIntSlot : BaseIntSlot() {
    override fun read(): Nothing = throw UnsupportedOperationException("read")
}
private class RejectingIndex<E>(items: List<E>) : List<E> by items {
    override fun indexOf(element: E): Nothing = throw UnsupportedOperationException("indexOf")
}

class CollectionAddResultTests {
    @TestAttribute
    fun authoredAddResultSurvivesEveryCollectionView() {
        val local = DotKt.Runtime.CompilerServices.KotlinMutableCollectionAddSlot("local")
        check(local.marker == "local")
        val rejecting: MutableSet<Int> = RejectingSet(mutableSetOf(1))
        try { rejecting.add(2); error("add returned") } catch (e: UnsupportedOperationException) { check(e.message == "add") }
        try { rejecting.addAll(listOf(2)); error("addAll returned") } catch (e: UnsupportedOperationException) { check(e.message == "addAll") }
        val list: List<Int> = RejectingIndex(listOf(1))
        try { list.indexOf(1); error("indexOf returned") } catch (e: UnsupportedOperationException) { check(e.message == "indexOf") }
        check(rejecting.size == 1)
        val inheritedSlots: PartialSet<String> = ReadOnlyStringSet()
        try { inheritedSlots.add("one"); error("add returned") } catch (e: UnsupportedOperationException) { check(e.message == "one") }
        val throughInterface: MutableSet<String> = inheritedSlots
        try { throughInterface.addAll(listOf("two")); error("addAll returned") } catch (e: UnsupportedOperationException) { check(e.message == "all") }
        // Collection syntax can lower to a collection helper; exercise direct base-slot dispatch too.
        val direct: BaseIntSlot = ThrowingIntSlot()
        try { direct.read(); error("read returned") } catch (e: UnsupportedOperationException) { check(e.message == "read") }

        val value = CollectionAddResultList(mutableListOf(1))
        check(value.add(2))
        val exact: MutableList<Int> = value
        check(exact.add(3))
        val projected: MutableList<in Int> = exact
        check(projected.add(4))
        val collection: MutableCollection<in Int> = exact
        check(collection.add(5))
        check(collectionAddResultGeneric(exact, 6))
        check(value.size == 1 && value[0] == 6 && value.calls == 5)

        var receiverReads = 0
        var argumentReads = 0
        val receiver = { receiverReads++; value }
        val argument = { argumentReads++; 7 }
        check(receiver().add(argument()))
        check(receiverReads == 1 && argumentReads == 1 && value.calls == 6)
        (value as System.Collections.Generic.ICollection<Int>).Add(8)
        check(value[0] == 8 && value.calls == 7)

        val inherited = CollectionAddResultInherited(mutableListOf("old"))
        check(collectionAddResultGeneric(inherited, "new") && inherited[0] == "new")
        val derived = CollectionAddResultDerived(mutableListOf(1))
        val base: CollectionAddResultList<Int> = derived
        check(!base.add(2) && !collectionAddResultGeneric(derived, 3) && derived.calls == 2 && derived[0] == 3)
        val defaults = CollectionAddResultDefaultCollection()
        check(collectionAddResultGeneric(defaults, 4) && defaults.values[0] == 4 && defaults.size == 1)
        val nullable = CollectionAddResultInherited(mutableListOf<Int?>(1))
        check(nullable.add(null) && nullable[0] == null)
        val first = Any()
        val second = Any()
        val objects = CollectionAddResultInherited(mutableListOf(first))
        check(collectionAddResultGeneric(objects, second) && objects[0] === second)
        val rejected = CollectionAddResultList(mutableListOf(1), false)
        check(!collectionAddResultGeneric(rejected, 9) && rejected[0] == 1 && rejected.calls == 1)

        val failure = IllegalStateException("authored add")
        value.failure = failure
        try {
            projected.add(9)
            error("add did not throw")
        } catch (caught: IllegalStateException) {
            check(caught === failure && value.calls == 8)
        }

        val nativeList = System.Collections.Generic.List<Int>()
        val nativeCollection = nativeList as MutableCollection<Int>
        check(nativeCollection.add(10) && nativeList.Count == 1)
        val nativeDerived = CollectionAddResultNativeList()
        check((nativeDerived as MutableCollection<Int>).add(12) && nativeDerived.Count == 1)
        val nativeSet = System.Collections.Generic.HashSet<Int>()
        val projectedSet = nativeSet as MutableSet<in Int>
        check(projectedSet.add(11) && !projectedSet.add(11) && nativeSet.Count == 1)
    }
}
