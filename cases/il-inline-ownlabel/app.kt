// #75 S4a §8.4 — forEach { return@forEach }: the return targets the forEach lambda ITSELF (the seed) -> LOCAL ->
// NOT an escape -> forEach takes the DELEGATE path (a real generic method taking a closure). The `return@forEach`
// is a continue-like early exit of the closure's invoke, and the trailing accumulation still runs per element.
fun main() {
    var kept = 0
    listOf(1, -2, 3, -4, 5).forEach {
        if (it < 0) return@forEach
        kept += it
    }
    println(kept)   // 1 + 3 + 5 = 9
}
