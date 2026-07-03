// A generic class inheriting a generic base instantiated over its OWN type parameter:
// `class D<T> : Base<T>()`. The base-ctor call must target the CONSTRUCTED base `Base<!T>`,
// not the open definition `Base<>` — otherwise ilemit emits a "not fully instantiated" ctor.
// This is exactly the shape of the cold-core `SequenceBuilderIterator<T> : SequenceScope<T>()`.
open class Base<T>(val x: T) {
    open fun show(): T = x
}

class D<T>(v: T) : Base<T>(v) {
    fun twice(): String = "${show()}/${x}"
}

fun main() {
    val d = D(42)
    println(d.x)        // 42
    println(d.show())   // 42
    println(d.twice())  // 42/42
    val s = D("hi")
    println(s.x)        // hi
}
