// C5/C11 gate regression guard: gating the universal-method intercept keeps collection toString routing
// Kotlin-style (`[a, b]`), tuple/data-class toString correct, and (C5) `String.hashCode()` deterministic
// + reproducible (the polynomial body, not .NET's randomized GetHashCode). NOTE: a nested collection
// INSIDE Pair/Triple.toString still renders the raw .NET type name — a stdlib/bir2cir gap (generic-value
// stringification is not collection-aware), NOT a kotc fix.
data class Rec(val name: String, val n: Int)
fun main() {
    println(listOf(1, 2, 3).toString())
    println(setOf(1, 2, 3).toString())
    println((1 to 2).toString())
    println(Triple(1, 2, 3))
    println(Rec("k", 9))
    println("Aa".hashCode() == "Aa".hashCode())
    println("Aa".hashCode())
}
