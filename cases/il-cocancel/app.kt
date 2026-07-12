// #86 P0 (stdlib-scoped) — RootContinuation.resumeWith (the ASYNC-resume sink of the generated
// suspend->Task<R> bridge) completes a CANCELLED result as a CANCELED Task, not a FAULTED one (.NET
// TaskCompletionSource convention). We drive resumeWith DIRECTLY — exactly what the bridge does on resume —
// because awaiting alone CANNOT tell the two apart: a faulted-with-OCE Task and a canceled Task BOTH rethrow
// the OperationCanceledException on await; only the completion STATE (IsCanceled vs IsFaulted) differs.
//
// SCOPE (two deferred gaps, not regressions of this fix):
//  - The bridge's own SYNC-throw path (an exception before the first suspension) faults via the bridge's
//    catch -> TrySetException (bir2cir SuspendColdLowering), NOT through resumeWith — so an end-to-end
//    `await(pre-cancelled Task)` would still fault. Mapping that to Canceled is a separate bir2cir follow-up.
//  - Subtype match: a real async cancellation arrives as TaskCanceledException (a BCL subtype of OCE); the
//    fix relies on `is OperationCanceledException` -> isinst matching subtypes. Not gated here because a
//    facadegen BCL exception isn't accepted where kotlin.Throwable is expected (Result.failure).
import kotlin.clr.TaskCompletionSource
import kotlin.clr.OperationCanceledException
import kotlin.coroutines.clr.internal.RootContinuation

fun main() {
    // (1) OperationCanceledException -> CANCELED (the P0 fix). Was Faulted before.
    val tcs1 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs1).resumeWith(Result.failure(OperationCanceledException()))
    println(tcs1.task.isCanceled)   // True
    println(tcs1.task.isFaulted)    // False

    // (2) a plain failure still FAULTS (the special-case must not over-broaden).
    val tcs2 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs2).resumeWith(Result.failure(IllegalStateException("boom")))
    println(tcs2.task.isCanceled)   // False
    println(tcs2.task.isFaulted)    // True

    // (3) success still completes with the value.
    val tcs3 = TaskCompletionSource<Int>()
    RootContinuation<Int>(tcs3).resumeWith(Result.success(42))
    println(tcs3.task.result)       // 42
}
