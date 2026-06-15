interface Greeter {
	fun greet(): String
}

class English : Greeter {
	override fun greet(): String = "Hello"
}

class Japanese : Greeter {
	override fun greet(): String = "Konnichiwa"
}

enum class Color { RED, GREEN, BLUE }

fun colorName(c: Color): String = when (c) {
	Color.RED -> "red"
	Color.GREEN -> "green"
	else -> "blue"
}

fun describe(g: Greeter): String = "greet: ${g.greet()}"

fun main() {
	println(describe(English()))
	println(describe(Japanese()))
	println("GREEN is ${colorName(Color.GREEN)}")
	println("RED is ${colorName(Color.RED)}")
}
