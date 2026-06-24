// IL parity: fold -> Aggregate, joinToString -> String.Join<T>.
fun main() {
    val xs = listOf(1, 2, 3, 4)
    println(xs.fold(0) { acc, x -> acc + x })
    println(xs.joinToString("-"))
    println(xs.joinToString())
    println(xs.map { it * 10 }.fold(0) { a, b -> a + b })
}
