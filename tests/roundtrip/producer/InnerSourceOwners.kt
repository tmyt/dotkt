package roundtrip.innerowner

open class Base<T>(val value: T)
class PlainInline<T>(val value: T) {
    inline fun visit(block: (T) -> Unit) { block(value) }
}
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
    inner class Inline<I>(val items: List<I?>) {
        inline fun visit(block: (I?) -> Unit) { block(items[0]) }
    }
}
class CompanionOuter<O>(value: O?) : Base<O?>(value) {
    inner class Middle<M>(value: M?) : Base<M?>(value) {
        inner class Leaf<L>(value: L?) : Base<L?>(value)
    }
}
fun <O, M, L> readCompanionLeaf(value: CompanionOuter<O>.Middle<M>.Leaf<L>): L? = value.value
fun <O, I> readOverloadedInner(value: NullableOuter<O>.Inner<I>): I? = value.value
fun <O, I> readOverloadedInner(value: Base<I?>): I? = value.value
class NullableInnerHolder(value: NullableOuter<Int>.Inner<String>) : Base<NullableOuter<Int>.Inner<String>>(value)
class Outer<O> {
    inner class Wrapped<I>(value: List<I>) : Base<List<I>>(value)
    open inner class NestedBase<I>(val nested: I)
    inner class Derived<I>(value: I) : NestedBase<I>(value)
    inner class Inner<I>(value: I) : Base<I>(value) {
        inline fun visit(block: (I) -> Unit) { block(value) }
    }
    inner class Captured(value: O) : Base<O>(value)
    inner class Middle<M> {
        inner class Leaf<L>(value: L) : Base<L>(value)
        inner class OuterValue(value: O) : Base<O>(value)
        inner class MiddleValue(value: M) : Base<M>(value)
    }
}
