// A: top-level properties (const val / val / var), vararg -> params, subjectless `when`.
const val GREETING = "hi"
val MAX = 100
var counter = 0

fun sumAll(vararg xs: Int): Int {
    var s = 0
    for (x in xs) s += x
    return s
}

fun grade(n: Int): String = when {
    n >= 90 -> "A"
    n >= 80 -> "B"
    else -> "C"
}

fun main() {
    println(GREETING)
    println(MAX)
    counter = counter + 5
    println(counter)
    println(sumAll(1, 2, 3, 4))
    println(grade(85))
}
