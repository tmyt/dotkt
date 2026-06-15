interface Greeter {
	fun greet(): String
}
class English : Greeter {
	override fun greet(): String = "Hello"
}
enum class Color { RED, GREEN, BLUE }
fun colorName(c: Color): String = when (c) {
	Color.RED -> "r"
	Color.GREEN -> "g"
	else -> "b"
}
