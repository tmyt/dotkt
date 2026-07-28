// The LOOP-CARRIED half of the liveness pair. `prev` is written late in one iteration and read at the top of the
// next, so it is live across the suspension on the loop's back edge and must be refused. Its twin in
// tests/coroutines/fixtures/ByRefLikeStorageTests.kt writes and reads the value within a single iteration and
// compiles: the difference is real liveness, which a lexical "declared before, read after" interval cannot see.
import System.Span

suspend fun cfLoopTick(n: Int): Int = n + 1

suspend fun cfLoopCarried(): Int {
    var acc = 0
    var prev = Span<Int>(arrayOf(0))
    for (i in 0 until 3) {
        acc += prev.Length
        prev = Span<Int>(arrayOf(1, 2))
        acc += cfLoopTick(i)
    }
    return acc
}

suspend fun main() {
    println(cfLoopCarried())
}
