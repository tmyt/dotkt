// IL parity: kotlin.text String ops -> System.String instance methods (clrInstance lowering).
fun main() {
    println("Hello".uppercase())
    println("Hello".lowercase())
    println("  hi  ".trim())
    println("hello".substring(1))
    println("hello".startsWith("he"))
    println("hello".contains("ell"))
}
