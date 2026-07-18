// C11 gate regression guard: gating the universal-method intercept keeps collection toString routing
// Kotlin-style (`[a, b]`), tuple/data-class toString correct, and (C5) `String.hashCode()` falling
// through to its declared @ClrIntrinsic override (CLR-native GetHashCode, #167) rather than being
// shadowed to System.Object — asserted here as within-run consistency, NOT a pinned hash value. A
// nested collection INSIDE Pair/Triple.toString is collection-aware too (C11) — see il-pairnest.
data class Rec(val name: String, val n: Int)
fun main() {
    println(listOf(1, 2, 3).toString())
    println(setOf(1, 2, 3).toString())
    println((1 to 2).toString())
    println(Triple(1, 2, 3))
    println(Rec("k", 9))
    println("Aa".hashCode() == "Aa".hashCode())
}
