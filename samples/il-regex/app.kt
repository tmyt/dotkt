// kotlin.text.Regex -> System.Text.RegularExpressions.Regex. `toRegex()`, `containsMatchIn`, `replace`.
fun main() {
    val digits = "\\d+".toRegex()
    println(digits.containsMatchIn("abc123"))     // true
    println(digits.containsMatchIn("no nums"))    // false
    println(digits.replace("a1b22c333", "#"))     // a#b#c#
    val ws = "\\s+".toRegex()
    println(ws.replace("a  b   c", "_"))          // a_b_c
}
