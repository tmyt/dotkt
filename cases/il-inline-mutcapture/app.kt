// #75 S4a §8.5 — a mutable captured var written from a NON-escaping forEach lambda. No escape -> the DELEGATE path,
// which ref-cell-boxes `sum`/`count` (computeRefCells) so the write-through is visible after the loop. Pins that the
// non-escaping-lambda delegate path preserves mutable-capture semantics (the LINQ model, per docs/dotkt-semantics).
fun main() {
    var sum = 0
    var count = 0
    listOf(10, 20, 30).forEach {
        sum += it
        count++
    }
    println(sum)     // 60
    println(count)   // 3
}
