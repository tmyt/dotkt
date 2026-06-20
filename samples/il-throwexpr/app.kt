// IL parity: `throw` in expression position (Nothing type) + exception type mapping.
fun pick(x: Int): String {
	val s = if (x > 0) "pos" else throw IllegalStateException("neg")
	return s
}
fun req(x: Int?): Int = x ?: throw IllegalArgumentException("null")
fun guard(x: Int): Int {
	if (x < 0) throw RuntimeException("neg")
	return x
}
fun main() {
	println(pick(5))
	println(req(42))
	println(guard(3))
}
