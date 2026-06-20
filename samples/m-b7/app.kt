// B: String.split + mapOf (Dictionary).
fun main() {
	val parts = "a,b,c".split(",")
	println(parts.size)
	println(parts.joinToString("|"))
	val m = mapOf("x" to 1, "y" to 2)
	println(m["x"])
	println(m.size)
}
