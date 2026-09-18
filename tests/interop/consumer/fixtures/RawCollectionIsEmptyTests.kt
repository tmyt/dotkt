import NUnit.Framework.TestAttribute
import CollectionStorageInterop.RawCollectionEmptiness
import CollectionStorageInterop.ForeignListFaces
import CollectionStorageInterop.ReifiedCollectionFaces

private fun rawListIsEmpty(value: Any): Boolean = (value as List<*>).isEmpty()
private fun rawMutableListIsEmpty(value: Any): Boolean = (value as MutableList<*>).isEmpty()
private fun rawCollectionIsEmpty(value: Any): Boolean = (value as Collection<*>).isEmpty()
private fun rawMutableCollectionIsEmpty(value: Any): Boolean = (value as MutableCollection<*>).isEmpty()
private class EmptyReceiverEvaluation(private val value: Any) {
    var calls = 0
    fun next(): Any { calls++; return value }
}

class RawCollectionIsEmptyTests {
    @TestAttribute fun genericGetterFailureDoesNotFallThroughToRawCount() {
        val value = RawCollectionEmptiness.GenericFailure()
        var caught: Any? = null
        try { rawListIsEmpty(value) } catch (failure: System.Exception) { caught = failure }
        check(caught === value.Failure)
        check(value.GenericReads == 1 && value.RawReads == 0)
    }

    @TestAttribute fun rawReadOnlyListIteratorsAndSubListsUseTheAvailableCount() {
        val value = RawCollectionEmptiness.List(false)
        val list = value as List<*>
        val iterator = list.listIterator()
        check(iterator.next() == 7 && iterator.next() == 9 && !iterator.hasNext())
        check(iterator.previous() == 9)
        val slice = list.subList(1, 2)
        check(slice.size == 1 && slice[0] == 9 && !slice.isEmpty())
    }

    @TestAttribute fun rawMapEmptinessUsesTheSharedCountCapability() {
        for (empty in arrayOf(false, true)) {
            val value = RawCollectionEmptiness.Map(empty)
            check((value as Map<*, *>).isEmpty() == empty)
        }
    }

    @TestAttribute fun genericReadOnlyCountIsNotReplacedByUnrelatedRawCount() {
        val value = RawCollectionEmptiness.MixedCount()
        check((value as List<*>).size == 2)
        check(!rawListIsEmpty(value))
    }

    @TestAttribute fun rawListsUseTheirCountForEmptyAndNonEmptyValues() {
        for (empty in arrayOf(false, true)) {
            val value = RawCollectionEmptiness.List(empty)
            check(rawListIsEmpty(value) == empty)
            check(rawMutableListIsEmpty(value) == empty)
            check(rawCollectionIsEmpty(value) == empty)
            check(rawMutableCollectionIsEmpty(value) == empty)
        }
    }

    @TestAttribute fun rawCollectionsUseTheirCountWithoutAListFace() {
        for (empty in arrayOf(false, true)) {
            val value = RawCollectionEmptiness.Collection(empty)
            check(rawCollectionIsEmpty(value) == empty)
            check(rawMutableCollectionIsEmpty(value) == empty)
        }
    }

    @TestAttribute fun genericOnlyAndReadOnlyFacesKeepTheirBehavior() {
        check(!rawListIsEmpty(ForeignListFaces.Generic()))
        check(!rawMutableListIsEmpty(ForeignListFaces.Generic()))
        check(!rawListIsEmpty(ForeignListFaces.ReadOnly()))
        check(!rawCollectionIsEmpty(ReifiedCollectionFaces.Collection()))
        check(!rawCollectionIsEmpty(ReifiedCollectionFaces.ReadOnlyCollection()))
    }

    @TestAttribute fun rawCountIsReadOnceWithoutEnumeration() {
        for (empty in arrayOf(false, true)) {
            val value = RawCollectionEmptiness.Counted(empty)
            check(rawListIsEmpty(value) == empty)
            check(value.Reads == 1)
            check(rawMutableListIsEmpty(value) == empty)
            check(value.Reads == 2)
            check(rawCollectionIsEmpty(value) == empty)
            check(value.Reads == 3)
        }
    }

    @TestAttribute fun rawCountFailurePropagatesTheOriginalException() {
        for (value in arrayOf(RawCollectionEmptiness.Counted(false), RawCollectionEmptiness.InvocationFailure())) {
            value.ThrowOnCount = true
            var caught: Any? = null
            try { rawListIsEmpty(value) } catch (failure: System.Exception) { caught = failure }
            check(caught === value.Failure)
            check(value.Reads == 1)
        }
    }

    @TestAttribute fun projectedReceiverIsEvaluatedOnce() {
        val value = RawCollectionEmptiness.Counted(false)
        val source = EmptyReceiverEvaluation(value)
        check(!(source.next() as List<*>).isEmpty())
        check(source.calls == 1 && value.Reads == 1)
    }
}
