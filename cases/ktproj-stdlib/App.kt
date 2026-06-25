// Exercises a stdlib op MIGRATED off the COLLECTION_OPS lowering: `getOrElse` now resolves to the real Kotlin body
// shipped in DotKt.Stdlib (auto-referenced by the targets), not the retired LINQ lowering.
fun main() {
    val xs = listOf(10, 20, 30)
    println(xs.getOrElse(1) { it * 100 })   // 20  (in range)
    println(xs.getOrElse(5) { it * 100 })   // 500 (out of range -> default)
}
