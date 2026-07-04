// Gate for the generic secondary-constructor delegation fix. A `constructor(...) : this(...)`
// delegation INSIDE a generic class must reference the sibling ctor through the self-instantiation
// `C<T>`, not the open type definition `C`1` — a bare `call C`1::.ctor` JIT-crashes with
// "not fully instantiated" (System.InvalidOperationException). This is the isolated repro of the
// stdlib RingBuffer<T> crash that made `listOf(...).windowed(3)` fail (ilemit EmitCtorBody thisArgs).
// Covered: value-type (Int) AND reference-type (String) instantiations, both reached via the
// delegating secondary ctor; plus a two-type-param generic to exercise the multi-arg self-instantiation.
class Ring<T>(val buffer: Array<Any?>, val filled: Int) {
    constructor(capacity: Int) : this(arrayOfNulls<Any?>(capacity), 0)
    fun cap(): Int = buffer.size
}

class Pair2<A, B>(val a: Any?, val b: Any?, val tag: Int) {
    constructor(a: Any?, b: Any?) : this(a, b, 7)
    fun tagged(): Int = tag
}

fun main() {
    val ri = Ring<Int>(3)        // value-type instantiation via delegating ctor
    val rs = Ring<String>(5)     // reference-type instantiation via delegating ctor
    println("${ri.cap()},${ri.filled}")   // 3,0
    println("${rs.cap()},${rs.filled}")   // 5,0
    val p = Pair2<Int, String>(1, "x")    // two-type-param generic, delegating ctor
    println(p.tagged())                    // 7
}
