package nullableselfbound

import NUnit.Framework.TestAttribute

private class Cell<T>(var value: T)
private open class Node<N : Node<N>>(previous: N?) {
    val previous = Cell<N?>(previous)
}
private abstract class Segment<S : Segment<S>>(previous: S?) : Node<S>(previous)
private class Concrete(previous: Concrete?) : Segment<Concrete>(previous)
private class SegmentResult<S : Segment<S>>(val value: S)

private fun <S : Segment<S>> wrap(value: S): SegmentResult<S> = SegmentResult(value)
private fun <Unused, S : Segment<S>> wrapLater(value: S): SegmentResult<S> = SegmentResult(value)
private fun <S : Segment<S>> deferred(value: S): () -> SegmentResult<S> = { SegmentResult(value) }

private class Factory<T>(val tag: T) {
    fun <S : Segment<S>> wrap(value: S): SegmentResult<S> = SegmentResult(value)
}

class NullableSelfBoundTests {
    @TestAttribute
    fun selfBoundRetainsItsNullableCompanion() {
        val first = Concrete(null)
        val second = Concrete(first)
        check(wrap(first).value === first)
        check(wrapLater<String, Concrete>(second).value === second)
        check(second.previous.value === first)
        check(first.previous.value == null)
    }

    @TestAttribute
    fun capturedSelfBoundRetainsItsConstraint() {
        val value = Concrete(null)
        check(deferred(value)().value === value)
    }

    @TestAttribute
    fun existentialOwnerPreservesAnIndependentSelfBound() {
        val factory: Factory<*> = Factory("tag")
        val value = Concrete(null)
        check(factory.wrap(value).value === value)
        check(factory.tag == "tag")
    }
}
