import clr.StringBuilder

// Uses ONLY the auto-generated façade (build/gen-facades/clr/StringBuilder.kt). No hand-written façade.
fun main() {
	val sb = StringBuilder()
	sb.append("Hello").append(", ").append("CLR ").append(42).append(' ').append(true)
	println(sb.toString())
	println("length = ${sb.length}")
}
