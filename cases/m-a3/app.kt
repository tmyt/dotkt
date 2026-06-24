// B/A: zip (-> value tuples), char range membership, Char.code.
fun main() {
	val pairs = listOf(1, 2, 3).zip(listOf("a", "b", "c"))
	println(pairs.size)
	println(pairs[0].second)
	println('B' in 'A'..'Z')
	println('a'.code)
}
