// bundle-6 ④ fix #1: String.compareTo is ORDINAL (by UTF-16 code unit), not culture-sensitive. The stdlib rule-3
// ordinal body (builtins/String.kt) is hoisted + used, so uppercase (< 'a') sorts before lowercase — matching JVM.
fun main() {
    println("a".compareTo("B"))        // 31  ('a'=97 - 'B'=66)
    println("B".compareTo("a"))        // -31
    println("abc".compareTo("abc"))    // 0
    println("abc".compareTo("abd"))    // -1
    println("a" < "B")                 // false (ordinal: 'a' > 'B')
    println("Z".compareTo("a"))        // -7  ('Z'=90 - 'a'=97), uppercase sorts before lowercase
}
