// Kotlin `Char.minus(Char): Int` (not Char) — `'a' - 'B'` is 31, printed as a number, NOT the invisible
// control glyph U+001F. `Char.plus(Int)`/`Char.minus(Int)` still return Char (arithmetic on a code point).
fun main() {
    println('a' - 'B')   // Char - Char -> Int: 97 - 66 = 31
    println('z' - 'a')   // Char - Char -> Int: 25
    println('a' + 1)     // Char + Int  -> Char: 'b'
    println('c' - 1)     // Char - Int  -> Char: 'b'
}
