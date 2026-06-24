// B: String->number parse, Char predicates, coerce* -> Math.*.
fun main() {
	println("42".toInt() + 1)
	println('a'.isLetter())
	println('5'.isDigit())
	println(10.coerceAtMost(7))
	println(3.coerceAtLeast(5))
	println(8.coerceIn(1, 5))
}
