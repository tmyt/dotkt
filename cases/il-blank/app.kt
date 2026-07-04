// IL parity: isBlank/isNotBlank run the pure-Kotlin stdlib body (index loop, NO kotc IsNullOrWhiteSpace lowering).
fun main() {
    println("".isBlank())         // True
    println("   ".isBlank())      // True
    println("a b".isBlank())      // False
    println("  x ".isNotBlank())  // True
    println("\t\n".isBlank())     // True
}
