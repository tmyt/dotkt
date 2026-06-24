// IL parity: String->number parse (Int32.Parse etc.) + Char predicates (System.Char.*).
fun main() {
    println("42".toInt() + 8)
    println("3.5".toDouble())
    println('7'.isDigit())
    println('a'.isLetter())
    println('x'.uppercaseChar())
}
