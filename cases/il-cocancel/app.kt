// #86 P0 (stdlib-scoped) — RootContinuation.resumeWith (the ASYNC-resume sink of the generated
// suspend->Task<R> bridge) completes a CANCELLED result as a CANCELED Task, not a FAULTED one (.NET
// TaskCompletionSource convention). We drive resumeWith DIRECTLY — exactly what the bridge does on resume —
// because awaiting alone CANNOT tell the two apart: a faulted-with-OCE Task and a canceled Task BOTH rethrow
// the OperationCanceledException on await; only the completion STATE (IsCanceled vs IsFaulted) differs.
//
// The bridge's own SYNC-throw path (an exception before the first suspension — e.g. `await` of an
// already-cancelled Task) is now ALSO routed through RootContinuation.resumeWith (#109, bir2cir
// SuspendColdLowering BuildBridge/RootResumeFailure): its catch calls `__root.resumeWith(Result.failure(e))`
// instead of `__tcs.TrySetException(e)`, so the SYNC throw funnels through the SAME OCE->trySetCanceled test
// this case exercises. The end-to-end `await(pre-cancelled Task)` bridge-Task-state assertion needs a .NET
// consumer of the Task<R> bridge (the bridge is not Kotlin-reachable) — a ktproj/roundtrip scenario, not this
// il-gate case; this case remains the unit proof of the routed resumeWith(Result.failure(OCE)) -> Canceled op.
//
// SUBTYPE match note: a real async cancellation arrives as TaskCanceledException (a BCL subtype of OCE); the
// fix relies on `is OperationCanceledException` -> isinst matching subtypes (exercised only in the .NET-consumer
// end-to-end path, since a facadegen BCL exception isn't accepted where kotlin.Throwable is expected here).
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
