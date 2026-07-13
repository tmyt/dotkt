// #3: `await(captureContext = false)` opt-out. bir2cir lowers `Task.await(captureContext = false)` to the
// `task.ConfigureAwait(false).GetAwaiter()` awaiter dance (ConfiguredTaskAwaitable.ConfiguredTaskAwaiter STRUCT calls —
// IsCompleted / OnCompleted / GetResult), whose OnCompleted does NOT capture the SynchronizationContext. This case
// exercises the SYNC FAST PATH (already-completed task): it validates the ConfigureAwait(false) awaiter-type
// resolution + struct-call lowering end-to-end (ilverify-clean). `.await()` (default, capturing) is covered by
// il-taskawait.
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun cfgAwait(): Int {
    Task.CompletedTask.await(captureContext = false)   // ConfigureAwait(false) awaiter, already-completed -> fast path
    return 5
}

fun main() {
    println(blockOn { cfgAwait() })   // 5
}
