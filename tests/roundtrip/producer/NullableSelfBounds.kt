package roundtrip.nullableselfbound

class Cell<T>(var value: T)
open class Node<N : Node<N>>(previous: N?) {
    val previous = Cell<N?>(previous)
}
abstract class Segment<S : Segment<S>>(previous: S?) : Node<S>(previous)
class Concrete(previous: Concrete?) : Segment<Concrete>(previous)
class SegmentResult<S : Segment<S>>(val value: S)

fun <S : Segment<S>> wrap(value: S): SegmentResult<S> = SegmentResult(value)
fun <Unused, S : Segment<S>> wrapLater(value: S): SegmentResult<S> = SegmentResult(value)
fun <S : Segment<S>> deferred(value: S): () -> SegmentResult<S> = { SegmentResult(value) }

class Factory<T>(val tag: T) {
    fun <S : Segment<S>> wrap(value: S): SegmentResult<S> = SegmentResult(value)
}
