fun pick(a: String?, b: String): String = a ?: b
fun force(a: String?): String = a!!

fun main() {
	println(pick(null, "fallback"))
	println(pick("present", "fallback"))
	println(force("forced"))
}
