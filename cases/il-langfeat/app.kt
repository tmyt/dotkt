// A-track language-feature sweep: anonymous fun, infix, tailrec, try-finally (fall-through), abstract class
// (virtual dispatch through an abstract base), destructuring in a lambda parameter.

val add = fun(a: Int, b: Int): Int = a + b                 // anonymous function

infix fun Int.pow(e: Int): Int {                           // infix function
    var r = 1; var i = 0; while (i < e) { r *= this; i++ }; return r
}

tailrec fun fact(n: Int, acc: Int): Int =                  // tailrec (plain recursion on the CLR)
    if (n <= 1) acc else fact(n - 1, acc * n)

fun withFinally(): String {                                // try/finally as a STATEMENT, then fall through
    var log = ""
    try { log += "t" } finally { log += "f" }
    return log
}

abstract class Shape(val name: String) {                   // abstract class + abstract member
    abstract fun area(): Int
    fun describe(): String = "$name=${area()}"
}
class Sq(val s: Int) : Shape("sq") { override fun area(): Int = s * s }
class Circle(val r: Int) : Shape("circle") { override fun area(): Int = 3 * r * r }

fun main() {
    println(add(3, 4))                                     // 7
    println(2 pow 10)                                      // 1024
    println(fact(5, 1))                                    // 120
    println(withFinally())                                 // tf
    val sh: Shape = Circle(2)                              // base-typed -> virtual dispatch
    println(sh.describe())                                 // circle=12
    println(Sq(5).describe())                              // sq=25
    listOf(1 to "a", 2 to "b").forEach { (n, s) -> println("$n$s") }   // destructuring lambda param: 1a, 2b
}
