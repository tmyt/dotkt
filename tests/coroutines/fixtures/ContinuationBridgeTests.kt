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
//                   (the bridge point); its former println side effect is captured into `unitContinuationLog` and asserted.
//   regression   -> unitContinuationList
//                   compiling a generic List<T>-returning public suspend declaration synthesizes one coherent
//                   readonly CLR result slot across Task<T>, TCS<T>, RootContinuation<T>, and TrySetResult(T).
//
// Top-level names use the descriptive `synchronousContinuation` and `unitContinuation` stems so they remain
// readable and cannot clash with sibling coroutine fixtures or the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import dotkt.support.blockOn

// il-suspendco: a synchronous resume — the block resumes immediately with 42 (never actually suspends).
suspend fun synchronousContinuationResume(): Int = suspendCoroutine { it.resume(42) }

// il-suspendco: a synchronous resumeWithException — getOrThrow() rethrows the failure at the (sync) point.
suspend fun synchronousContinuationResumeWithException(): Int = suspendCoroutine { it.resumeWithException(IllegalStateException("boom")) }

// il-counit: a PUBLIC Unit-returning suspend fun -> a non-generic `Task` bridge. `unitContinuationStep` completes
// synchronously, so `unitContinuationGreet` genuinely suspends then resumes on the sync path, exercising the full
// state-machine + Unit Task-bridge emit. The former println("hello 42") is captured into unitContinuationLog.
suspend fun unitContinuationStep(): Int = 21
var unitContinuationLog: String = ""
suspend fun unitContinuationGreet(): Unit {
    val x = unitContinuationStep()
    unitContinuationLog = "hello " + (x * 2)
}

suspend fun <T> unitContinuationList(value: T): List<T> = listOf(value)

class ContinuationBridgeTests {
    @TestAttribute
    fun syncResume() {
        assertEquals(42, blockOn { synchronousContinuationResume() })   // 42
    }

    @TestAttribute
    fun syncResumeWithException() {
        var msg: String? = null
        try {
            blockOn { synchronousContinuationResumeWithException() }
        } catch (e: IllegalStateException) {
            msg = e.message
        }
        assertEquals("boom", msg)                // former golden: "caught:boom"
    }

    @TestAttribute
    fun unitReturningSuspendTaskBridge() {
        unitContinuationLog = ""
        blockOn { unitContinuationGreet() }
        assertEquals("hello 42", unitContinuationLog)          // former golden: "hello 42" (then main printed "done")
    }
}
