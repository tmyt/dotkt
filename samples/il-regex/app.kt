// kotlin.text.Regex -> System.Text.RegularExpressions.Regex. toRegex / containsMatchIn / replace / matches / find.
fun main() {
    val digits = "\\d+".toRegex()
    println(digits.containsMatchIn("abc123"))     // true
    println(digits.containsMatchIn("no nums"))    // false
    println(digits.replace("a1b22c333", "#"))     // a#b#c#
    val ws = "\\s+".toRegex()
    println(ws.replace("a  b   c", "_"))          // a_b_c

    val num = "[0-9]+".toRegex()
    println(num.matches("12345"))                 // true  (whole input)
    println(num.matches("12a45"))                 // false (not whole input)
    println(num.find("abc42def")?.value)          // 42
    println(num.find("nodigits")?.value)          // null
}
