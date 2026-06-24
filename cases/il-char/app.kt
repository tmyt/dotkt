// Char operations -> System.Char statics / code-point casts. (kotlin.text Char predicates & case conversions.)
fun main() {
    val c = 'a'
    println(c.isLetter())            // true
    println('7'.isDigit())           // true
    println(' '.isWhitespace())      // true
    println(c.isLetterOrDigit())     // true
    println(c.uppercaseChar())       // A
    println('Z'.lowercaseChar())     // z
    println('Q'.isUpperCase())       // true
    println(c.isLowerCase())         // true
    println(c.code)                  // 97  (Char -> Int code point)
    println(98.toChar())             // b   (Int -> Char)
}
