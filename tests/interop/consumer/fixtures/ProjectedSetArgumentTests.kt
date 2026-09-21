package interop.setarguments

import NUnit.Framework.TestAttribute
import SetArgumentInterop.*

private fun widenedSize(value: Set<Any?>): Int = value.size
private fun widenedView(value: Set<Any?>): Set<Any?> = value
private fun forwardSize(value: Set<*>): Int = widenedSize(value)
private fun forwardView(value: Set<*>): Set<Any?> = widenedView(value)
private fun nullableSize(value: Set<Any?>?): Int = value?.size ?: -1
private fun forwardNullable(value: Set<*>?): Int = nullableSize(value)
private fun collectionSize(value: Collection<Any?>): Int = value.size
private fun ignoreCollection(value: Collection<Any?>): Int = 7
private fun ignoreSet(value: Set<Any?>): Int = 9
private fun ignoreIterable(value: Iterable<Any?>): Int = 11
private fun iterableView(value: Iterable<Any?>): Iterable<Any?> = value
private fun nullableIterableView(value: Iterable<Any?>?): Iterable<Any?>? = value
private fun forwardNullableIterable(value: Set<*>?): Iterable<Any?>? = nullableIterableView(value)
private fun iterableSum(value: Iterable<Any?>): Int {
    var sum = 0
    for (element in value) sum += element as Int
    return sum
}

private class AuthoredArgumentIterator : Iterator<Int> {
    private var value: Int = 7
    override fun hasNext(): Boolean = value <= 11
    override fun next(): Int {
        if (!hasNext()) throw NoSuchElementException()
        val result = value
        value += 2
        return result
    }
}

private class AuthoredArgumentSet : AbstractSet<Int>() {
    override val size: Int get() = 3
    override fun iterator(): Iterator<Int> = AuthoredArgumentIterator()
    override fun isEmpty(): Boolean = true
    override fun contains(element: Int): Boolean = element == 99
    override fun containsAll(elements: Collection<Int>): Boolean = false
}

class ProjectedSetArgumentTests {
    @TestAttribute fun ambiguousSetsCanBePassedWithoutOpeningTheirElementFamily() {
        val source: Any = ObjectAndIntSet()
        val set = source as Set<*>
        check(ignoreCollection(set) == 7)
        check(ignoreSet(set) == 9)
        check(ignoreIterable(set) == 11)
        val view = forwardView(set)
        var caught = false
        try { view.iterator() }
        catch (_: IllegalStateException) { caught = true }
        check(caught)
    }

    @TestAttribute fun exactMutableWitnessKeepsFaithfulReadonlyIdentity() {
        val source: Any = ObjectSetAndIntReadonlySet()
        val exact = source as MutableSet<Any?>
        check(widenedSize(exact) == 2)
        check(widenedView(exact) === source)
    }

    @TestAttribute fun exactMutableWitnessSurvivesRequiredReadonlyAdaptation() {
        val source: Any = ObjectMutableSetAndIntSet()
        val exact = source as MutableSet<Any?>
        val view = widenedView(exact)
        check(view !== source)
        check(view.size == 2)
        check(view.contains("a") && !view.contains(7))
        val iterator = view.iterator()
        var count = 0
        while (iterator.hasNext()) {
            check(iterator.next() is String)
            count++
        }
        check(count == 2)
        exact.add("c")
        check(view.size == 3 && view.contains("c"))
    }

    @TestAttribute fun unrelatedValueListDoesNotMakeTheSetAmbiguous() {
        val source: Any = IntSetAndList<Long>()
        check(forwardSize(source as Set<*>) == 3)
    }

    @TestAttribute fun unrelatedReferenceListDoesNotSupplyCount() {
        val source: Any = IntSetAndList<String>()
        check(forwardSize(source as Set<*>) == 3)
    }

    @TestAttribute fun iterationUsesTheSetParentNotRawOrUnrelatedEnumerables() {
        val source: Any = IntSetAndList<String>()
        val iterator = forwardView(source as Set<*>).iterator()
        var count = 0
        var sum = 0
        while (iterator.hasNext()) { sum += iterator.next() as Int; count++ }
        check(count == 3 && sum == 27)
    }

    @TestAttribute fun queriesUseTheSetElements() {
        val source: Any = IntSetAndList<String>()
        val view = forwardView(source as Set<*>)
        check(!view.isEmpty())
        check(view.contains(7))
        check(!view.contains("raw-list"))
        check(view.containsAll(listOf<Any?>(7, 9)))
    }

    @TestAttribute fun viewRemainsLive() {
        val source = IntSetAndList<String>()
        val erased: Any = source
        val view = forwardView(erased as Set<*>)
        check(view.size == 3)
        source.Add(13)
        check(view.size == 4 && view.contains(13))
    }

    @TestAttribute fun faithfulReferenceCovariancePreservesIdentity() {
        val source: Any = StringSetAndList<Long>()
        check(forwardView(source as Set<*>) === source)
        check(forwardSize(source as Set<*>) == 3)
    }

    @TestAttribute fun anExactUnrelatedReferenceFaceDoesNotWinCovariance() {
        val source: Any = StringSetAndList<Any?>()
        val view = forwardView(source as Set<*>)
        check(view.size == 3)
        check(view.contains("set-a"))
        check(!view.contains(null))
    }

    @TestAttribute fun aMutableOnlySetGetsAReadonlyArgumentView() {
        val source: Any = IntSetOnly()
        val view = forwardView(source as Set<*>)
        check(view.size == 3 && view.contains(9))
    }

    @TestAttribute fun authoredOverridesRemainAuthoritative() {
        val view: Set<*> = forwardView(AuthoredArgumentSet())
        check(view.size == 3) { "authored size" }
        check(view.isEmpty()) { "authored isEmpty" }
        check(view.contains(99)) { "authored contains hit" }
        check(!view.contains(7)) { "authored contains miss" }
        check(!view.containsAll(emptyList())) { "authored containsAll" }
        check(view.iterator().next() == 7) { "authored iterator" }
    }

    @TestAttribute fun anAlreadyAdaptedSetDoesNotGetWrappedAgain() {
        val source: Any = IntSetAndList<String>()
        val view = forwardView(source as Set<*>)
        check(forwardView(view) === view)
    }

    @TestAttribute fun nullableArgumentsPreserveNullAndAdaptNonNullSets() {
        check(forwardNullable(null) == -1)
        val source: Any = IntSetAndList<String>()
        check(forwardNullable(source as Set<*>) == 3)
    }

    @TestAttribute fun anExactAuthoredWitnessDoesNotBecomeAnAmbiguousStar() {
        val source: Any = ObjectAndIntSet()
        val exact = source as Set<Any?>
        check(widenedSize(exact) == 2)
        check(widenedView(exact) === source)
    }

    @TestAttribute fun genuinelyAmbiguousSetFamiliesRemainAmbiguous() {
        val source: Any = ObjectAndIntSet()
        var caught = false
        try { forwardSize(source as Set<*>) }
        catch (_: IllegalStateException) { caught = true }
        check(caught)
    }

    @TestAttribute fun aSetWidenedToCollectionKeepsItsSelectedFamily() {
        val source: Any = IntSetAndList<String>()
        check(collectionSize(source as Set<*>) == 3)
    }

    @TestAttribute fun aSetWidenedToIterableKeepsItsSelectedEnumeration() {
        val source: Any = IntSetAndList<String>()
        check(iterableSum(source as Set<*>) == 27)
    }

    @TestAttribute fun faithfulIterableCovarianceDoesNotRequireReadonlyCollection() {
        val source: Any = StringSetOnly()
        check(iterableView(source as Set<*>) === source)
    }

    @TestAttribute fun nullableIterableBoundaryPreservesNullAndItsSetElements() {
        check(forwardNullableIterable(null) == null)
        val source: Any = IntSetAndList<String>()
        val result = forwardNullableIterable(source as Set<*>)!!
        var sum = 0
        for (element in result) sum += element as Int
        check(sum == 27)
    }

    @TestAttribute fun concreteMutableStorageCanFillAReadonlySetParameter() {
        val source: Any = ObjectSetOnly()
        val mutable = source as MutableSet<Any?>
        check(widenedSize(mutable) == 2)
    }
}
