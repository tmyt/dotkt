package roundtrip.innerowner

open class Base<T>(val value: T)
class NullableOuter<O>(val outer: O?) {
    inner class Inner<I>(value: I?) : Base<I?>(value) { val captured = outer }
    inner class Middle<M>(val middle: M?) {
        inner class Leaf<L>(value: L?) : Base<L?>(value) {
            val capturedOuter = outer
            val capturedMiddle = middle
        }
        inner class Captured(value: O?) : Base<O?>(value)
    }
}
class NullableOwnOuter<O> {
    inner class Inner<I>(value: I?) : Base<I?>(value)
}
class Outer<O> {
    inner class Wrapped<I>(value: List<I>) : Base<List<I>>(value)
    open inner class NestedBase<I>(val nested: I)
    inner class Derived<I>(value: I) : NestedBase<I>(value)
    inner class Inner<I>(value: I) : Base<I>(value)
    inner class Captured(value: O) : Base<O>(value)
    inner class Middle<M> {
        inner class Leaf<L>(value: L) : Base<L>(value)
        inner class OuterValue(value: O) : Base<O>(value)
        inner class MiddleValue(value: M) : Base<M>(value)
    }
}
