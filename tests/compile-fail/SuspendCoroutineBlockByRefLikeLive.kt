// The negative half of the inlined-intrinsic pair. `suspendCoroutine { … }`'s block is reconstructed inline and
// its closure class deleted, so a byref-like value the block merely READS is an ordinary local of this frame and
// compiles (tests/coroutines/fixtures/ByRefLikeStorageTests.kt). Here the same value is read AFTER the intrinsic
// suspension, so it is live across it and the ordinary storage gate refuses it — the CS4007 mirror, not the
// closure-capture CS8352 one, because there is no closure class in the emitted program to blame.
import System.Span
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

suspend fun cfIntrinsicLive(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4))
    val v = suspendCoroutine { c -> c.resume(1) }
    return v + s.Length
}

suspend fun main() {
    println(cfIntrinsicLive())
}
