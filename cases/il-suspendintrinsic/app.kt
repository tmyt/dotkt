// #80 (COROUTINE_SUSPENDED intrinsic resolution) — a low-level suspend fun whose intrinsic block reads the
// TOP-LEVEL VAL `COROUTINE_SUSPENDED` directly. MemberCallSubstitution mis-owns that top-level val to the enclosing
// file class (`AppKt.get_COROUTINE_SUSPENDED`), which ilemit cannot resolve; bir2cir canonicalizes any such read to
// the SM's own Suspended() marker in Rewrite/RewriteNoSpill (lifted out of the F2-only SubstBlock so it covers every
// SM-body path, including this direct user read of the intrinsic that flows through Rewrite, not SubstBlock).
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.resume
import dotkt.support.blockOn

// The uninterceptedOrReturn intrinsic passes the state machine itself as `c`; a synchronous `c.resume(v)` buffers the
// value, and the block returns the top-level-val COROUTINE_SUSPENDED — the exact read #80 is about.
suspend fun syncResume(v: Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    c.resume(v)
    COROUTINE_SUSPENDED
}

fun main() {
    println(blockOn { syncResume(42) })   // 42
}
