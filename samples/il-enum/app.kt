enum class Color { RED, GREEN, BLUE }

fun colorName(c: Color): String = when (c) {
	Color.RED -> "red"
	Color.GREEN -> "green"
	else -> "blue"
}

fun main() {
	println(colorName(Color.RED))
	println(colorName(Color.GREEN))
	println(colorName(Color.BLUE))
}
