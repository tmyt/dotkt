// IL parity: forEach (enumerator loop) — captures via closure, iterates a List.
fun main() {
    val xs = listOf(10, 20, 30)
    var sum = 0
    xs.forEach { sum = sum + it }
    println(sum)
    var n = 0
    xs.map { it / 10 }.forEach { n = n + it }
    println(n)
}
