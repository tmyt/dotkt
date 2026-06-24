// IL parity: requireNotNull / checkNotNull (reference + value-nullable).
fun firstChar(s: String?): Char = requireNotNull(s)[0]
fun must(n: Int?): Int = checkNotNull(n)
fun main() {
	println(firstChar("hello"))
	println(must(7))
}
