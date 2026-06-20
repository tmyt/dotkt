// IL parity: Pair (a to b) -> ValueTuple, .first/.second, destructuring.
fun main() {
    val p = 3 to 4
    println(p.first)
    println(p.second)
    val q = "x" to 10
    println(q.first)
    println(q.second)
    val (a, b) = 5 to 6
    println(a + b)
}
