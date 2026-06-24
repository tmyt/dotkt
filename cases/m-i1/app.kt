import clr.StringBuilder

fun main() {
	val sb = StringBuilder()
	sb.append("Hello").append(", ").append("CLR ").append(42)
	println(sb.toString())
	println("length = ${sb.length}")
}
