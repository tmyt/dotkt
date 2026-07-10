enum class Color { RED, GREEN, BLUE }

inline fun <reified T : Enum<T>> pick(i: Int): T = enumValues<T>()[i]

fun main() {
	println(enumValues<Color>()[1]); println(enumValues<Color>().size)
	println(enumValueOf<Color>("BLUE").ordinal)
	for (c in enumValues<Color>()) println(c); println(pick<Color>(2))
}
