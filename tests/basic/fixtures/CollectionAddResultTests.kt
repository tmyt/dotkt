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

private fun <T> collectionAddResultGeneric(receiver: MutableCollection<T>, element: T): Boolean = receiver.add(element)

class CollectionAddResultTests {
    @TestAttribute
    fun authoredAddResultSurvivesEveryCollectionView() {
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
