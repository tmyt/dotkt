// CorB batch — the COROUTINE_SUSPENDED intrinsic-resolution family. A direct user read of the top-level val
// `kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED` — canonicalized to the SM's Suspended() marker on an
// SM-body path (suspendintrinsic), and bound through the GENERAL owner-null top-level resolver on a non-suspend
// reader path (suspendintrinsicowned). Each former case's `main` + stdout-golden becomes one @TestAttribute
// method preserving every value 1:1 (`// <expected>`).
//
// Coverage preserved (old case -> method):
//   il-suspendintrinsic      -> suspendintrinsic_intrinsicReadInSuspendBlock   (#80: Rewrite canonicalizes to Suspended())
//   il-suspendintrinsicowned -> suspendintrinsicowned_nonSuspendMemberReadIdentity (#157: getResult()-shape identity read)
//
// (il-xmodtopval — the pure CROSS-MODULE top-level-val resolution guard that merely USES COROUTINE_SUSPENDED as
// its vehicle, with no coroutine runtime — is NOT migrated here; it is flagged for the tests/interop lane.)
//
// Top-level names carry a per-case token (`sint`/`sio`) under the shared `corB`/`CorB` prefix so they can't clash
// with sibling coroutine fixtures or the stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

// ---- il-suspendintrinsic -------------------------------------------------------------------------------------
// The uninterceptedOrReturn intrinsic passes the state machine itself as `c`; a synchronous `c.resume(v)` buffers
// the value, and the block returns the top-level-val COROUTINE_SUSPENDED — the exact read #80 is about.
suspend fun corBSintSyncResume(v: Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    c.resume(v)
    COROUTINE_SUSPENDED
}

// ---- il-suspendintrinsicowned --------------------------------------------------------------------------------
// Mirrors DispatchedCoroutine.getResult(): a non-suspend member returning the sentinel as Any?.
class CorBSioHolder {
    fun getState(): Any? = COROUTINE_SUSPENDED
}

class CoroutineIntrinsicTests {
    @TestAttribute
    fun intrinsicReadInSuspendBlock() {
        assertEquals(42, blockOn { corBSintSyncResume(42) })   // 42
    }

    @TestAttribute
    fun nonSuspendMemberReadIdentity() {
        val h = CorBSioHolder()
        // Both reads resolve to the SAME cached CoroutineSingletons.COROUTINE_SUSPENDED box -> reference identity holds,
        // exactly as getResult()'s own `state === COROUTINE_SUSPENDED` check relies on.
        assertEquals(42, if (h.getState() === COROUTINE_SUSPENDED) 42 else 0)   // 42
    }
}
