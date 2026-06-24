import clr.StringBuilder

fun main() {
	val sb = StringBuilder()
	sb.append("built via ").append("dotnet build").append(" + facade for ").append(42)
	println(sb.toString())
}
