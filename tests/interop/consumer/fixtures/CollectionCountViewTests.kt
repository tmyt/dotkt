import NUnit.Framework.TestAttribute
import CollectionStorageInterop.RawCollectionEmptiness

private fun collectionViewCount(value: Collection<*>): Int = value.size
private fun collectionViewEmpty(value: Collection<*>): Boolean = value.isEmpty()
private fun mutableCollectionViewCount(value: MutableCollection<*>): Int = value.size
private fun listUpcastCount(value: Any): Int {
    check(value is MutableList<*>)
    val collection: Collection<*> = value
    return collection.size
}
private fun setUpcastCount(value: Any): Int {
    check(value is MutableSet<*>)
    val collection: Collection<*> = value
    return collection.size
}
private class KotlinListWithDictionary : IterableClassifierStorage.ListAndDictionary()
private class KotlinSetWithDictionary : IterableClassifierStorage.SetAndDictionary()

class CollectionCountViewTests {
    @TestAttribute fun mutableCollectionUsesOnlyItsMutableClosure() {
        val value: Any = CollectionStorageInterop.MutableListAndReadOnlyCollection()
        check((value as MutableCollection<*>).size == 2)
        check(mutableCollectionViewCount(value as MutableCollection<*>) == 2)
    }

    @TestAttribute fun unrelatedReadOnlyAndMutableClosuresRemainAmbiguous() {
        val value: Any = CollectionStorageInterop.MutableListAndReadOnlyCollection()
        var caught = false
        try { collectionViewCount(value as Collection<*>) } catch (failure: System.InvalidOperationException) {
            caught = true
        }
        check(caught)
    }

    @TestAttribute fun listAndCollectionUseTheSameGenericCountBeforeRawStorage() {
        val value: Any = CollectionStorageInterop.RawAndGenericList()
        val list = value as List<*>
        check(list.size == 2)
        check(list[0] == 7)
        check(!list.isEmpty())
        val collection: Collection<*> = list
        check(collection.size == 2)
        check(collectionViewCount(collection) == 2)
    }

    @TestAttribute fun rawListCountPreservesNativeInvocationException() {
        val raw = RawCollectionEmptiness.InvocationFailure()
        raw.ThrowOnCount = true
        val value: Any = raw
        var caught: Any? = null
        try { (value as List<*>).size } catch (failure: System.Exception) { caught = failure }
        check(caught === raw.Failure)
        check(raw.Reads == 1)
    }

    @TestAttribute fun explicitObjectCollectionCastAndLocalKeepTheExactSlot() {
        val value: Any = CollectionStorageInterop.ObjectCollectionAndIntList()
        check((value as Collection<Any>).size == 3)
        val collection = value as Collection<Any>
        check(collection.size == 3)
    }

    @TestAttribute fun independentCollectionClosureIsNotDiscardedByAList() {
        val value: Any = CollectionStorageInterop.ListAndIndependentCollection()
        var caught = false
        try { collectionViewCount(value as Collection<*>) } catch (failure: System.InvalidOperationException) {
            caught = true
        }
        check(caught)
    }

    @TestAttribute fun orderedDictionaryStorageDoesNotCompeteWithIndependentList() {
        val value: Any = CollectionStorageInterop.OrderedDictionaryWithList()
        check(collectionViewCount(value as Collection<*>) == 1)
        check(!collectionViewEmpty(value as Collection<*>))
        check((value as List<*>).size == 1)
        check((value as List<*>)[0] == "list")
    }

    @TestAttribute fun rawIndependentListDoesNotUseDictionaryCount() {
        val value: Any = CollectionStorageInterop.RawListAndDictionary()
        check((value as Collection<*>).size == 2)
        check(collectionViewCount(value as Collection<*>) == 2)
        check(!collectionViewEmpty(value as Collection<*>))
    }

    @TestAttribute fun multipleIndependentCollectionClosuresRemainAmbiguous() {
        val value: Any = CollectionStorageInterop.AmbiguousCollectionCounts()
        var caught = false
        try { collectionViewCount(value as Collection<*>) } catch (failure: System.InvalidOperationException) {
            caught = true
        }
        check(caught)
    }

    @TestAttribute fun explicitClosedCollectionViewDisambiguatesTheCount() {
        val value: Any = CollectionStorageInterop.AmbiguousCollectionCounts()
        val integers = value as Collection<Int>
        val strings = value as Collection<String>
        check(integers.size == 2)
        check(strings.size == 3)
        val projected: Collection<*> = integers
        check(projected.size == 2)
    }

    @TestAttribute fun listUpcastExcludesDictionaryEntryCount() {
        val value = IterableClassifierStorage.ListAndDictionary()
        value.Add(7)
        value.Add(9)
        check(listUpcastCount(value) == 2)
    }

    @TestAttribute fun setUpcastExcludesDictionaryEntryCount() {
        val value = IterableClassifierStorage.SetAndDictionary()
        value.Add(7)
        value.Add(9)
        check(setUpcastCount(value) == 2)
    }

    @TestAttribute fun collectionParametersUseTheIndependentCountAndDefaultEmptiness() {
        val list = IterableClassifierStorage.ListAndDictionary()
        val set = IterableClassifierStorage.SetAndDictionary()
        list.Add(7)
        set.Add(9)
        for (value in arrayOf<Any>(list, set)) {
            check(collectionViewCount(value as Collection<*>) == 1)
            check(!collectionViewEmpty(value as Collection<*>))
        }
    }

    @TestAttribute fun explicitCollectionCastsUseTheIndependentCount() {
        val list = IterableClassifierStorage.ListAndDictionary()
        list.Add(7)
        val value: Any = list
        check((value as Collection<*>).size == 1)
        check(!(value as Collection<*>).isEmpty())
    }

    @TestAttribute fun kotlinSubclassesKeepTheInheritedCollectionContract() {
        val list = KotlinListWithDictionary()
        val set = KotlinSetWithDictionary()
        list.Add(7)
        (set as MutableSet<Int>).add(9)
        check(listUpcastCount(list) == 1)
        check(setUpcastCount(set) == 1)
        check(!collectionViewEmpty(list as Collection<*>))
        check(!collectionViewEmpty(set as Collection<*>))
    }

    @TestAttribute fun rawCountDoesNotReplaceTheIndependentGenericCollection() {
        val value = RawCollectionEmptiness.MixedCount()
        check((value as Collection<*>).size == 2)
        check(collectionViewCount(value as Collection<*>) == 2)
        check(!collectionViewEmpty(value as Collection<*>))
    }

    @TestAttribute fun genericCountFailureIsNotMaskedByRawCount() {
        val value = RawCollectionEmptiness.GenericFailure()
        var caught: Any? = null
        try { collectionViewCount(value as Collection<*>) } catch (failure: System.Exception) { caught = failure }
        check(caught === value.Failure)
        check(value.GenericReads == 1 && value.RawReads == 0)
    }

    @TestAttribute fun concreteCollectionArgumentsKeepTheirExactSlot() {
        val value: Collection<Int> = listOf(7, 9)
        check(value.size == 2)
        val projected: Collection<*> = value
        check(projected.size == 2)
    }
}
