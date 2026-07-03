// bundle-6 ① BUG 4 (bir2cir SuspendColdLowering DrainMain): a `suspend fun main` that GENUINELY suspends.
// main awaits a real INCOMPLETE .NET Task (Task.Delay), so main$dotkt_suspend returns COROUTINE_SUSPENDED and
// the resume lands on a threadpool thread. The synthesized plain `main` must drive the cold body under a REAL
// root continuation (RootContinuation<Unit> over a TaskCompletionSource<Unit>) and BLOCK on tcs.Task until the
// resume completes. With the old null-completion drive the resume dereferenced null (NRE / lost output). The
// leading sync println proves the drain does not stall the fast path; the post-await println proves it blocks
// until the async resume (else main would exit before "42" is printed).
import System.Threading.Tasks.Task
import kotlin.clr.await

suspend fun compute(): Int {
    Task.Delay(1).await()   // genuine suspension: resumes on a threadpool thread
    return 42
}

suspend fun main() {
    println("start")
    val x = compute()
    println(x)   // 42 — printed only if the drain BLOCKS until the async resume
}
