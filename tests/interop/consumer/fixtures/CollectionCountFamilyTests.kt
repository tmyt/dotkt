import NUnit.Framework.TestAttribute

private fun familyListEmpty(value: List<*>): Boolean = value.isEmpty()
private fun familySetSize(value: Set<*>): Int = value.size
private fun familyMutableSetSize(value: MutableSet<*>): Int = value.size
private fun familySetEmpty(value: Set<*>): Boolean = value.isEmpty()
private fun familyMutableSetEmpty(value: MutableSet<*>): Boolean = value.isEmpty()
private class FamilyEmptyOverride : List<Int> by listOf(7, 9) {
    var calls = 0
    override fun isEmpty(): Boolean { calls++; return true }
}
private class FamilyOwnedSet : Set<Int> by setOf(7, 9)
private class FamilyOwnedMutableSet : MutableSet<Int> by mutableSetOf(7, 9)

class CollectionCountFamilyTests {
    @TestAttribute fun kotlinSetImplementationsKeepTheirCollectionAbi() {
        for (set in arrayOf<Set<*>>(setOf(7, 9), emptySet<Int>(), FamilyOwnedSet())) {
            check(familySetSize(set) == if (set.isEmpty()) 0 else 2)
        }
        val value: Any = FamilyOwnedSet()
        check((value as Set<*>).size == 2)
    }

    @TestAttribute fun kotlinMutableSetsAndMapViewsKeepTheirCollectionAbi() {
        val owned = FamilyOwnedMutableSet()
        check(familyMutableSetSize(owned) == 2)
        check(familyMutableSetSize(mutableSetOf(7, 9)) == 2)
        val map = mutableMapOf(7 to "a", 9 to "b")
        check(familySetSize(map.keys) == 2)
        check(familyMutableSetSize(map.keys) == 2)
        check(familySetSize(map.entries) == 2)
    }

    @TestAttribute fun listEmptinessKeepsTheListFamily() {
        val value: Any = CollectionStorageInterop.ListAndIndependentCollection()
        val list = value as List<*>
        check(list === value)
        check(list.size == 2)
        check(!list.isEmpty())
        check(!(value as List<*>).isEmpty())
        check(!familyListEmpty(list))
    }

    @TestAttribute fun listIteratorsKeepTheListFamily() {
        val value: Any = CollectionStorageInterop.ListAndIndependentCollection()
        val list = value as List<*>
        val iterator = list.listIterator()
        check(iterator.next() == 0)
        check(iterator.next() == 1)
        check(!iterator.hasNext())
        val end = list.listIterator(2)
        check(!end.hasNext() && end.previous() == 1)
    }

    @TestAttribute fun subListsKeepTheListFamilyAndBounds() {
        val value: Any = CollectionStorageInterop.ListAndIndependentCollection()
        val list = value as List<*>
        val sub = list.subList(1, 2)
        check(sub.size == 1 && sub[0] == 1)
        check(sub.subList(0, 1).listIterator().next() == 1)
        var rejected = false
        try { list.subList(0, 3) } catch (failure: IndexOutOfBoundsException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun setSizeAndEmptinessKeepTheSetFamily() {
        val value: Any = CollectionStorageInterop.MutableSetAndReadOnlyList()
        val mutableSet = value as MutableSet<*>
        val set: Set<*> = mutableSet
        check(mutableSet === value && set === value)
        check((value as MutableSet<*>).size == 3)
        check((value as Set<*>).size == 3)
        check(familySetSize(set) == 3 && familyMutableSetSize(mutableSet) == 3)
        check(!set.isEmpty() && !mutableSet.isEmpty())
        check(!familySetEmpty(set) && !familyMutableSetEmpty(mutableSet))
    }

    @TestAttribute fun mutableCollectionEmptinessUsesOnlyItsMutableContract() {
        val value: Any = CollectionStorageInterop.MutableListAndReadOnlyCollection()
        val collection = value as MutableCollection<*>
        check(collection.size == 2 && !collection.isEmpty())
    }

    @TestAttribute fun mutableListIteratorKeepsTheListFamily() {
        val value: Any = CollectionStorageInterop.MutableListAndMutableCollection()
        val list = value as MutableList<*>
        val iterator = list.listIterator()
        check(iterator.next() == 7)
        iterator.remove()
        check(list.size == 1 && iterator.next() == 9 && !iterator.hasNext())
    }

    @TestAttribute fun mutableSubListKeepsTheListFamily() {
        val value: Any = CollectionStorageInterop.MutableListAndMutableCollection()
        val list = value as MutableList<*>
        val sub = list.subList(0, 1)
        check(sub.size == 1 && sub[0] == 7)
        check(sub.removeAt(0) == 7)
        check(list.size == 1 && list[0] == 9)
    }

    @TestAttribute fun mutableListSizingExcludesDictionaryStorage() {
        val value: Any = CollectionStorageInterop.MutableListWithDictionaryStorage()
        val list = value as MutableList<*>
        check(list.size == 2)
        check(list.listIterator().hasNext())
        check(list.subList(0, 1).size == 1)
    }

    @TestAttribute fun collectionWideningStillReportsUnrelatedClosures() {
        val value: Any = CollectionStorageInterop.ListAndIndependentCollection()
        val list = value as List<*>
        val collection: Collection<*> = list
        var rejected = false
        try { collection.isEmpty() } catch (failure: IllegalStateException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun multipleListClosuresRemainAmbiguous() {
        val value: Any = CollectionStorageInterop.AmbiguousCollectionCounts()
        val list = value as List<*>
        var rejected = false
        try { list.isEmpty() } catch (failure: IllegalStateException) { rejected = true }
        check(rejected)
    }

    @TestAttribute fun kotlinEmptinessOverrideStillWinsOverCount() {
        val value = FamilyEmptyOverride()
        check(familyListEmpty(value))
        check(value.calls == 1)
        val opaque: Any = value
        check((opaque as List<*>).isEmpty())
        check(value.calls == 2)
    }
}
