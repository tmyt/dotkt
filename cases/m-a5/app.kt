// A: destructuring (Pair via to, data class via componentN), numeric conversions (toInt/toLong/toDouble).
data class P(val x: Int, val y: Int)

fun main() {
    val (a, b) = 3 to 4
    println(a + b)
    val (px, py) = P(7, 9)
    println(px * py)
    println(3.7.toInt())
    println(5.toLong())
    println(2.toDouble())
}
