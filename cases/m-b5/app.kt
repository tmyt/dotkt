// B: precondition/error helpers + Pair/to (value tuples).
fun half(n: Int): Int {
	require(n % 2 == 0)
	return n / 2
}
fun main() {
	println(half(10))
	val p = 3 to "three"
	println(p.first)
	println(p.second)
	val x = if (half(4) == 2) "ok" else error("bad")
	println(x)
}
