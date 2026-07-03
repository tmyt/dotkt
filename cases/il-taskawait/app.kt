// bundle-6 P4 REVERSE bridge — `.NET Task ⇒ Kotlin suspend`. The facadegen-injected `Task.await()`
// extension (kotlin.clr.CoroutinesKt.await, a suspendCall marker) is lowered by bir2cir
// (SuspendColdLowering.EmitAwaitPoint) into the cold-core awaiter dance:
//   aw = task.GetAwaiter(); if (aw.IsCompleted) <value = aw.GetResult()>          // sync fast path
//   else { aw.OnCompleted(<Action re-driving this SM>); return COROUTINE_SUSPENDED }
//
// This case exercises the SYNC FAST PATH: both awaited tasks are ALREADY completed, so IsCompleted is
// true and no suspension/threadpool callback happens — it validates the await marker lowering, the
// TaskAwaiter STRUCT calls (GetAwaiter / IsCompleted / GetResult, generic + non-generic void), the
// generic result read-back, and the blockOn drain of a synchronously-completing coroutine. (The genuine
// ASYNC resume path — OnCompleted → resumeWith — is exercised by il-cobuild, blocked on two cross-layer
// gaps; see cobuild's XFAIL_RUN reason.)
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import kotlin.clr.await
import kotlin.clr.blockOn

suspend fun genAwait(): Int {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(42)
    return tcs.Task.await() + 1        // generic Task<Int>.await(), already-completed -> fast path -> 43
}

suspend fun unitAwait(): Int {
    Task.CompletedTask.await()          // non-generic Task.await(): Unit, already-completed -> fast path
    return 7
}

fun main() {
    println(blockOn { genAwait() })     // 43
    println(blockOn { unitAwait() })    // 7
}
