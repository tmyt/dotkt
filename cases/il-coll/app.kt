// IL parity: collection ops via LINQ generics — map/filter/take/drop/any/all/count/first/contains/reversed.
fun main() {
    val xs = listOf(1, 2, 3, 4, 5)
    println(xs.size)
    println(xs.map { it * 2 }.size)
    println(xs.filter { it > 2 }.size)
    println(xs.take(2).size)
    println(xs.drop(2).size)
    println(xs.any { it > 4 })
    println(xs.all { it > 0 })
    println(xs.count { it > 2 })
    println(xs.first())
    println(xs.first { it > 3 })
    println(xs.contains(3))
    println(xs.reversed().first())
}
