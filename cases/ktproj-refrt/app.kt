fun main() {
    val xs = listOf("apple", "pear", "fig")
    println(xs.size)
    println(xs[0].uppercase())
    var t = 0
    for (s in xs) t += s.length
    println(t)
}
