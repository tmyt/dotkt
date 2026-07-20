// Cold-core suspend battery — migrates the `suspendCoroutine{}`-shaped and Unit-Task-bridge coroutine cases
// onto the in-process NUnit suite. These exercise the CLR cold Continuation state machine directly (no genuine
// .NET Task await — the sync-completion path), driven by the shared `dotkt.support.blockOn` harness. Each old
// case's `main` + stdout-golden becomes one @TestAttribute method preserving every asserted value 1:1 (see the
// `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-suspendco -> suspendco_syncResume / suspendco_syncResumeWithException
//                   cross-module `suspendCoroutine{}` -> caller cold SM; SafeContinuation UNDECIDED/RESUMED
//                   identity cache (F1) so a SYNC `it.resume(42)` holds and `it.resumeWithException` rethrows.
//   il-counit    -> counit_unitReturningSuspendTaskBridge
//                   a PUBLIC Unit-returning suspend fun -> a NON-generic public `Task` bridge (coroutine-abi.md
//                   §1: `suspend fun f(): Unit` maps to `Task`, not `Task<Unit>`). greet stays Unit-returning
//                   (the bridge point); its former println side effect is captured into `cuLog` and asserted.
//
// Top-level names are family-prefixed (`sco`/`cu`) so they can't clash with sibling coroutine fixtures or the
// stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import dotkt.support.blockOn

// il-suspendco: a synchronous resume — the block resumes immediately with 42 (never actually suspends).
suspend fun scoSc(): Int = suspendCoroutine { it.resume(42) }

// il-suspendco: a synchronous resumeWithException — getOrThrow() rethrows the failure at the (sync) point.
suspend fun scoScThrow(): Int = suspendCoroutine { it.resumeWithException(IllegalStateException("boom")) }

// il-counit: a PUBLIC Unit-returning suspend fun -> a non-generic `Task` bridge. `cuStep` completes
// synchronously, so `cuGreet` genuinely suspends then resumes on the sync path, exercising the full
// state-machine + Unit Task-bridge emit. The former println("hello 42") is captured into cuLog.
suspend fun cuStep(): Int = 21
var cuLog: String = ""
suspend fun cuGreet(): Unit {
    val x = cuStep()
    cuLog = "hello " + (x * 2)
}

class ColdCoreTests {
    @TestAttribute
    fun suspendco_syncResume() {
        assertEquals(42, blockOn { scoSc() })   // 42
    }

    @TestAttribute
    fun suspendco_syncResumeWithException() {
        var msg: String? = null
        try {
            blockOn { scoScThrow() }
        } catch (e: IllegalStateException) {
            msg = e.message
        }
        assertEquals("boom", msg)                // former golden: "caught:boom"
    }

    @TestAttribute
    fun counit_unitReturningSuspendTaskBridge() {
        cuLog = ""
        blockOn { cuGreet() }
        assertEquals("hello 42", cuLog)          // former golden: "hello 42" (then main printed "done")
    }
}
