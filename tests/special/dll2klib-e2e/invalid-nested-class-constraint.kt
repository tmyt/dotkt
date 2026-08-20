import Probe.ReferenceConstraintBox

open class NestedConstraintBase

class InvalidNestedOuter<T>(val value: T) {
    inner class InvalidNestedInner<U : NestedConstraintBase>(val other: U) {
        // The physical !0 is captured T. Reading only this inner declaration's own typeParams mistakes it for U.
        fun invalid(): Int = ReferenceConstraintBox<T>().Value
    }
}

fun main() {
    InvalidNestedOuter(1).InvalidNestedInner(NestedConstraintBase()).invalid()
}
