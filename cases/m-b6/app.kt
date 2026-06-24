// B: more collection ops (distinct/sorted/reduce/maxOrNull/joinToString) + setOf.
fun main() {
	val xs = listOf(3, 1, 2, 2, 4)
	println(xs.distinct().size)
	println(xs.sorted().joinToString("-"))
	println(xs.reduce { a, b -> a + b })
	println(xs.maxOrNull())
	println(setOf(1, 2, 2, 3).size)
	println(listOf("a", "b", "c").joinToString(", "))
}
