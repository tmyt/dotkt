// clr/taskinterop/: the CLR platform Task bridge (design note §5 — references the TaskCompletionSource
// alias). Lives under libraries/stdlib/clr/, so all three stdlib builds compile it (collect_stdlib_sources).
//
// The Task-bridge sink (bundle-6 P1, docs/design-coroutine-cold-core-task-bridge.md §2): the root
// [Continuation] of a hot `Task<T>` bridge over a cold suspend body. The generated bridge (P2) does
//   tcs = TaskCompletionSource<T>(); root = RootContinuation(tcs); r = f$dotkt_suspend(args, root)
//   if (r !== COROUTINE_SUSPENDED) complete tcs with r; return tcs.task
// and when the body suspends, the eventual resume lands here and completes the TCS: success ->
// TrySetResult, a CANCELLED failure (OperationCanceledException) -> TrySetCanceled (a CANCELED Task, .NET
// convention), any other failure -> TrySetException. Public: instantiated by generated code in APP assemblies.
@file:Suppress("UNCHECKED_CAST")

package kotlin.coroutines.clr.internal

import kotlin.clr.OperationCanceledException
import kotlin.clr.TaskCompletionSource
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

public class RootContinuation<T>(
    private val tcs: TaskCompletionSource<T>
) : Continuation<T> {

    public override val context: CoroutineContext
        get() = EmptyCoroutineContext

    public override fun resumeWith(result: Result<T>) {
        val exception = result.exceptionOrNull()
        when {
            exception == null -> tcs.trySetResult(result.value as T)
            // .NET fidelity: a CANCELLED result completes the Task as CANCELED (IsCanceled == true, await
            // rethrows the OCE cleanly), NOT FAULTED — matching TaskCompletionSource convention (#86 P0).
            // Pass the OCE's originating CancellationToken through (as AsyncTaskMethodBuilder does), so the
            // canceled Task carries the token that raised it (#116).
            exception is OperationCanceledException -> tcs.trySetCanceled(exception.cancellationToken)
            else -> tcs.trySetException(exception)
        }
    }
}
