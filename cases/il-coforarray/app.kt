// Locks suspend-fn control flow over a `for (e in ARRAY)` loop (a forArray BIR node) — Array<Int>, vararg,
// and IntArray, with a suspend call in the loop body. (Flagged as a suspected gap in family-6, but it lowers
// correctly: forArray-with-suspension is desugared before the SM segmentation, so the loop var + accumulator
// survive the suspension. Sync-completion drain via suspend main, mirroring il-coldcf CF4.)
suspend fun one(): Int = 1

suspend fun overArray(xs: Array<Int>): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + one() }
    return acc
}
suspend fun overVararg(vararg xs: Int): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + one() }
    return acc
}
suspend fun overIntArray(xs: IntArray): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + one() }
    return acc
}
suspend fun main() {
    println(overArray(arrayOf(10, 20, 30)))   // (10+1)+(20+1)+(30+1)=63
    println(overVararg(10, 20, 30))           // 63
    println(overIntArray(intArrayOf(1, 2, 3)))// (1+1)+(2+1)+(3+1)=9
}
