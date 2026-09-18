import NUnit.Framework.TestAttribute

class ConcreteCollectionCastTests {
    private fun collection(value: Any): Collection<Any> = value as Collection<Any>
    private fun safeCollection(value: Any): Collection<Any>? = value as? Collection<Any>
    private fun set(value: Any): Set<Any> = value as Set<Any>
    private fun safeSet(value: Any): Set<Any>? = value as? Set<Any>
    private fun mutableSet(value: Any): MutableSet<Any> = value as MutableSet<Any>
    private fun safeMutableSet(value: Any): MutableSet<Any>? = value as? MutableSet<Any>
    private fun nullableCollection(value: Any?): Collection<Any?>? = value as Collection<Any?>?
    private fun nullableSet(value: Any?): Set<Any?>? = value as Set<Any?>?
    private fun nullableMutableSet(value: Any?): MutableSet<Any?>? = value as MutableSet<Any?>?

    @TestAttribute fun collectionReturnsItsConcreteInterface() {
        val value: Any = listOf<Any>("a")
        check(collection(value).size == 1)
        check(safeCollection(value) === value)
        check(safeCollection(Any()) == null)
    }

    @TestAttribute fun setReturnsItsConcreteInterface() {
        val value: Any = setOf<Any>("a")
        check(set(value).size == 1)
        check(safeSet(value) === value)
        check(safeSet(Any()) == null)
    }

    @TestAttribute fun mutableSetReturnsItsConcreteInterface() {
        val value: Any = mutableSetOf<Any>("a")
        check(mutableSet(value).size == 1)
        check(safeMutableSet(value) === value)
        check(safeMutableSet(Any()) == null)
    }

    @TestAttribute fun nullableConcreteInterfacesKeepTheirResultTypes() {
        check(nullableCollection(null) == null)
        check(nullableSet(null) == null)
        check(nullableMutableSet(null) == null)
        check(nullableCollection(listOf<Any?>(null))!!.size == 1)
        check(nullableSet(setOf<Any?>(null))!!.size == 1)
        check(nullableMutableSet(mutableSetOf<Any?>(null))!!.size == 1)
    }

    private fun addThroughSmartCast(value: MutableIterable<Any?>) {
        if (value is MutableSet<Any?>) value.add("added")
    }

    @TestAttribute fun concreteSmartCastRetainsTheMemberReceiverInterface() {
        val value = mutableSetOf<Any?>(null)
        addThroughSmartCast(value)
        check(value.size == 2)
        check(value.contains("added"))
    }
}
