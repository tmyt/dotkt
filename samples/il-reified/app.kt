// IL parity: reified type params via targeted inline expansion.
inline fun <reified T> typeName(): String = T::class.simpleName ?: "?"
inline fun <reified T> isA(x: Any): Boolean = x is T
inline fun <reified T> asT(x: Any): String = (x as? T)?.toString() ?: "no"

fun main() {
	println(typeName<String>())
	println(typeName<Int>())
	println(isA<String>("hi"))
	println(isA<Int>("hi"))
	println(isA<Int>(42))
	println(asT<String>("yo"))
	println(asT<String>(7))
}
