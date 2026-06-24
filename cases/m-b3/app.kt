// B: more collection ops (fold/any/all/count/sum/first/take) via LINQ.
fun main() {
	val xs = listOf(1, 2, 3, 4, 5)
	println(xs.fold(0) { acc, e -> acc + e })
	println(xs.any { it > 4 })
	println(xs.all { it > 0 })
	println(xs.count { it % 2 == 0 })
	println(xs.sum())
	println(xs.first { it > 2 })
	println(xs.take(2).size)
}
