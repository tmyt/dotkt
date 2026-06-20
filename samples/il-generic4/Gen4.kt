// Generic method on a generic class (generic-on-generic), and a generic method returning its type param.
class Holder<T>(val value: T) {
    fun <R> pairWith(other: R): String = "$value & $other"
    fun get(): T = value
}

fun <A, B> firstOf(a: A, b: B): A = a

fun main() {
    val h = Holder(42)
    println(h.get())                 // 42
    println(h.pairWith("hi"))        // 42 & hi
    println(h.pairWith(99))          // 42 & 99
    println(firstOf("x", 7))         // x
}
