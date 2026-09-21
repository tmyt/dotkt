package interop.setarguments

import NUnit.Framework.TestAttribute
import SetArgumentInterop.*

private fun widenedSize(value: Set<Any?>): Int = value.size
private fun widenedView(value: Set<Any?>): Set<Any?> = value
private fun forwardSize(value: Set<*>): Int = widenedSize(value)
private fun forwardView(value: Set<*>): Set<Any?> = widenedView(value)

private class AuthoredArgumentSet : AbstractSet<Int>() {
    override val size: Int get() = 3
    override fun iterator(): Iterator<Int> = arrayOf(7, 9, 11).iterator()
    override fun isEmpty(): Boolean = true
    override fun contains(element: Int): Boolean = element == 99
    override fun containsAll(elements: Collection<Int>): Boolean = false
}

class ProjectedSetArgumentTests {
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
        check(view.containsAll(listOf(7, 9)))
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
    }

    @TestAttribute fun anAlreadyAdaptedSetDoesNotGetWrappedAgain() {
        val source: Any = IntSetAndList<String>()
        val view = forwardView(source as Set<*>)
        check(forwardView(view) === view)
    }
}
