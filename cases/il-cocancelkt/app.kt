// #105 (stdlib-scoped) — RootContinuation.resumeWith completes a Kotlin CancellationException result as a
// CANCELED Task, not a FAULTED one. Kotlin's kotlin.coroutines.cancellation.CancellationException extends
// IllegalStateException on CLR (CancellationExceptionClr.kt) — it is NOT a .NET OperationCanceledException — so
// before the fix the OCE-only clause missed it and the completing coroutine FAULTED its bridge Task. We drive
// resumeWith DIRECTLY (the exact bridge resume path), exactly as il-cocancel does for the OCE case, because the
// Task<R> bridge is not Kotlin-reachable for a completion-STATE assertion (an end-to-end .NET-consumer scenario
// belongs to ktproj/roundtrip); await alone cannot tell canceled from faulted (both rethrow), only IsCanceled can.
import kotlin.clr.TaskCompletionSource
import kotlin.coroutines.clr.internal.RootContinuation
import kotlin.coroutines.cancellation.CancellationException

fun main() {
    // (1) a Kotlin CancellationException -> CANCELED (the #105 fix). Was FAULTED before.
    val tcs1 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs1).resumeWith(Result.failure(CancellationException("stop")))
    println(tcs1.task.isCanceled)   // True
    println(tcs1.task.isFaulted)    // False

    // (2) a plain IllegalStateException (the CE SUPERTYPE on CLR) still FAULTS — proves the clause is specific
    //     to CancellationException, not over-broadened to every IllegalStateException.
    val tcs2 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs2).resumeWith(Result.failure(IllegalStateException("boom")))
    println(tcs2.task.isCanceled)   // False
    println(tcs2.task.isFaulted)    // True

    // (3) success still completes with the value.
    val tcs3 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs3).resumeWith(Result.success(7))
    println(tcs3.task.result)       // 7
}
