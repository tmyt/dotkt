// suspend->Task<R> bridge CANCELLATION-completion battery (CorA batch). RootContinuation.resumeWith (the async-resume
// sink of the generated bridge) completes a CANCELLED result as a CANCELED Task, not a FAULTED one. We drive
// resumeWith DIRECTLY — exactly what the bridge does on resume — because awaiting alone cannot tell canceled from
// faulted apart (both rethrow the OCE); only the completion STATE (IsCanceled vs IsFaulted) differs. Each old `main`
// + stdout golden becomes one @TestAttribute method asserting the completion state / value 1:1.
//
// Coverage preserved (old case -> method):
//   il-cocancel   -> coCancel_operationCanceledToCanceledTask   (#86 P0: .NET OperationCanceledException -> Canceled)
//   il-cocancelkt -> coCancelKt_kotlinCancellationToCanceledTask(#105: Kotlin CancellationException -> Canceled)
//
// Both assert the negative control (a plain failure still FAULTS) and the success value. No blockOn: the resume path
// is exercised directly on the root continuation. Top-level names live only inside the test methods (no prefix needed).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.clr.TaskCompletionSource
import kotlin.clr.OperationCanceledException
import kotlin.coroutines.clr.internal.RootContinuation
import kotlin.coroutines.cancellation.CancellationException

class CorACancelTests {
    @TestAttribute
    fun coCancel_operationCanceledToCanceledTask() {
        // (1) OperationCanceledException -> CANCELED (the P0 fix). Was Faulted before.
        val tcs1 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs1).resumeWith(Result.failure(OperationCanceledException()))
        assertEquals(true, tcs1.task.isCanceled)    // True
        assertEquals(false, tcs1.task.isFaulted)    // False

        // (2) a plain failure still FAULTS (the special-case must not over-broaden).
        val tcs2 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs2).resumeWith(Result.failure(IllegalStateException("boom")))
        assertEquals(false, tcs2.task.isCanceled)   // False
        assertEquals(true, tcs2.task.isFaulted)     // True

        // (3) success still completes with the value.
        val tcs3 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs3).resumeWith(Result.success(42))
        assertEquals(42, tcs3.task.result)          // 42
    }

    @TestAttribute
    fun coCancelKt_kotlinCancellationToCanceledTask() {
        // (1) a Kotlin CancellationException -> CANCELED (the #105 fix). Was FAULTED before.
        val tcs1 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs1).resumeWith(Result.failure(CancellationException("stop")))
        assertEquals(true, tcs1.task.isCanceled)    // True
        assertEquals(false, tcs1.task.isFaulted)    // False

        // (2) a plain IllegalStateException (the CE SUPERTYPE on CLR) still FAULTS.
        val tcs2 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs2).resumeWith(Result.failure(IllegalStateException("boom")))
        assertEquals(false, tcs2.task.isCanceled)   // False
        assertEquals(true, tcs2.task.isFaulted)     // True

        // (3) success still completes with the value.
        val tcs3 = TaskCompletionSource<Int>()
        RootContinuation<Int>(tcs3).resumeWith(Result.success(7))
        assertEquals(7, tcs3.task.result)           // 7
    }
}
