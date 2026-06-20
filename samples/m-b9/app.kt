// B: firstOrNull/lastOrNull/none/sumOf/maxByOrNull via LINQ.
fun main() {
	val xs = listOf(3, 1, 4, 1, 5)
	println(xs.firstOrNull { it > 3 })
	println(xs.lastOrNull())
	println(xs.none { it > 10 })
	println(xs.sumOf { it * 2 })
	println(xs.maxByOrNull { -it })
}
