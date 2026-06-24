// A: local (nested) functions, including closure capture of an enclosing parameter.
fun outer(n: Int): Int {
    fun square(x: Int): Int = x * x
    fun addSquares(a: Int, b: Int): Int = square(a) + square(b)
    return addSquares(n, n + 1)
}

fun withClosure(base: Int): Int {
    fun bump(x: Int): Int = x + base   // captures `base`
    return bump(10)
}

fun main() {
    println(outer(3))
    println(withClosure(5))
}
