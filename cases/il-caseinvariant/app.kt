// #144: String/Char uppercase()/lowercase() use CLR-native 1:1 case mapping
// (System.String.ToUpperInvariant/ToLowerInvariant) and DELIBERATELY do NOT perform the
// Unicode one-to-many full-mapping expansions (ß->SS, ligatures, ...) that Kotlin/JVM/Native/JS do.
// See docs/dotkt-semantics.md §5g.
fun main() {
    println("ß".uppercase())          // ß      (NOT "SS": no one-to-many expansion)
    println("straße".uppercase())     // STRAßE  (the ß stays ß)
    println("abc".uppercase())        // ABC    (normal 1:1 mapping works)
    println("HELLO".lowercase())      // hello
    println('ß'.uppercase())          // ß      (Char.uppercase(): String, no expansion)
    println("ß".uppercase() == "ß")   // True
}
