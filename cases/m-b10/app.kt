// B: groupBy / associateBy / associateWith -> Dictionary.
fun main() {
	val xs = listOf(1, 2, 3, 4)
	println(xs.groupBy { it % 2 }.size)
	println(xs.associateWith { it * it }[3])
	println(listOf("a", "bb", "ccc").associateBy { it.length }.size)
}
