// #177: an extension fun declared inside a companion object lowers to a static method whose first
// param `__self` is the extension receiver. The call site must pass that receiver as the LEADING arg
// (was dropped -> arity miscompile). Cover a no-arg ext, an ext taking a regular arg, and a
// generic/Int-receiver ext to exercise the receiver-prepend across arg shapes.
class C {
    companion object {
        fun String.f(): Int = length + 1
        fun String.g(delta: Int): Int = length + delta
        fun Int.tripled(): Int = this * 3
    }

    fun run() {
        println("abcd".f())        // 5
        println("abcd".g(10))      // 14
        println(7.tripled())       // 21
    }
}

fun main() {
    C().run()
}
