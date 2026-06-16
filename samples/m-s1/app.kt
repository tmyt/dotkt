fun pick(a: String?, b: String): String = a ?: b
fun force(a: String?): String = a!!
fun lenOr(a: String?): Int = a?.length ?: -1

fun main() {
	println(pick(null, "fallback"))
	println(pick("present", "fallback"))
	println(force("forced"))
	println("len null = ${lenOr(null)}")
	println("len hello = ${lenOr("hello")}")
}
