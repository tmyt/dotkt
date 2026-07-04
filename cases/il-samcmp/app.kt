// IL parity: an explicit `Comparator { a, b -> ... }` SAM conversion -> a synthetic class implementing the plain
// Kotlin fun interface (NO @ClrTypeAlias/@ClrIntrinsic read in kotc; Comparator is a pure Kotlin fun interface).
fun main() {
    val ns = mutableListOf(3, 1, 4, 1, 5, 9, 2, 6)
    ns.sortWith(Comparator { a, b -> a - b })
    println(ns.joinToString(","))                    // 1,1,2,3,4,5,6,9
    ns.sortWith(Comparator { a, b -> b - a })
    println(ns.joinToString(","))                    // 9,6,5,4,3,2,1,1
}
