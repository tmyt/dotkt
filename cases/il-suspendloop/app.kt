// #82 (splice-local spill across a resume) — a structured collection loop whose body spans a suspension. The loop's
// implicit machinery (array + index for forArray; a non-generic IEnumerator for forEachInline) and its element var
// cross the resume point, so bir2cir's FlattenSuspendingLoops desugars the loop to flat label/brIf/goto CFG with those
// temps made explicit `{k:var}` — CollectVarFields then spills them into SM fields (the `load unknown var __inlsN$…`
// root). Covers BOTH the vararg/array shape (joinAll/flowOf) and the inline-forEach-over-Iterable shape (asFlow).
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun awaitDouble(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension inside the loop body
    return x * 2
}

// forArray — `for (x in <IntArray>)`; x and the loop index cross the resume.
suspend fun sumArray(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        acc += awaitDouble(x)
    }
    return acc
}

// forEachInline — inline `Iterable.forEach { }` splices to `for (x in xs)` over a List (GetEnumerator loop); the
// splice-generated element local crosses the resume.
suspend fun sumList(xs: List<Int>): Int {
    var acc = 0
    xs.forEach { x ->
        acc += awaitDouble(x)
    }
    return acc
}

// break/continue crossing the resume: a suspending forArray loop with `continue` (skip 0) and `break` (stop at
// first negative), exercising RewriteBreakContinue's goto rewrite to the flattened loop's cont/end labels.
suspend fun sumUntilNeg(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        if (x == 0) continue
        if (x < 0) break
        acc += awaitDouble(x)
    }
    return acc
}

fun main() {
    println(blockOn { sumArray(intArrayOf(1, 2, 3)) })          // 12  (2 + 4 + 6)
    println(blockOn { sumList(listOf(4, 5)) })                  // 18  (8 + 10)
    println(blockOn { sumUntilNeg(intArrayOf(1, 0, 2, -1, 9)) }) // 6   (2 + skip + 4 + break)
}
