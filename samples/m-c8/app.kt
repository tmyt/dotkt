// C-1: nullable value types (Int? -> int?), with null checks and elvis.
fun firstOrZero(xs: List<Int>): Int {
	val v: Int? = if (xs.isEmpty()) null else xs[0]
	return v ?: 0
}
fun main() {
	val a: Int? = null
	println(a == null)
	println(firstOrZero(listOf(7, 8)))
	println(firstOrZero(emptyList()))
}
