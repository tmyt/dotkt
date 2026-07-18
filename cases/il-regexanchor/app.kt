// #162: Regex.matchEntire/matches must return a FULL anchored match, not a leftmost search filtered by span.
// A shorter alternation branch (`a` in `a|ab`) or a lazy quantifier must still yield the full-input match.
fun main() {
    println(Regex("a|ab").matchEntire("ab")?.value)         // ab
    println(Regex("a|ab").matches("ab"))                    // True
    println(Regex("a|ab").matchEntire("a")?.value)          // a
    println(Regex("a+?").matchEntire("aaa")?.value)         // aaa   (lazy quantifier forced to fill)
    println(Regex("").matchEntire("")?.value == "")         // True  (empty pattern, empty input)
    println(Regex("(\\d+)-(\\d+)").matchEntire("12-34")?.groupValues?.joinToString(",")) // 12-34,12,34
    println(Regex("^ab$").matchEntire("ab")?.value)         // ab    (existing anchors coexist)
    println(Regex("(?i)abc").matches("ABC"))                // True  (compiled options honored)
    println(Regex("[0-9]+").matches("12a45"))               // False (not the whole input)
    println(Regex("a").matchEntire("ab")?.value)            // null  (partial, not full)
}
