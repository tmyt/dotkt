// clr/taskinterop/: jar-EXCLUDED, CLR-build-ONLY (design note §5 — references the TaskCompletionSource
// alias; build-stdlib-jar.sh skips this dir, build-stdlib-{ref,rt}.sh compile it).
//
// The Task-bridge sink (bundle-6 P1, docs/design-coroutine-cold-core-task-bridge.md §2): the root
// [Continuation] of a hot `Task<T>` bridge over a cold suspend body. The generated bridge (P2) does
//   tcs = TaskCompletionSource<T>(); root = RootContinuation(tcs); r = f$dotkt_suspend(args, root)
//   if (r !== COROUTINE_SUSPENDED) complete tcs with r; return tcs.task
// and when the body suspends, the eventual resume lands here and completes the TCS: success ->
// TrySetResult, failure -> TrySetException. Public: instantiated by generated code in APP assemblies.
@file:Suppress("UNCHECKED_CAST")

package kotlin.coroutines.clr.internal

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
        if (exception != null) {
            tcs.trySetException(exception)
        } else {
            tcs.trySetResult(result.value as T)
        }
    }
}
