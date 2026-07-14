// #7 Part B — await-point resume PRECEDENCE: interceptor > captured SynchronizationContext > inline.
// When the coroutine context carries a ContinuationInterceptor, a `Task.await()` resume routes through
// ContinuationImpl.intercepted() (the interceptor-wrapped continuation), so the interceptor decides the
// resume thread/context — taking precedence over the raw SynchronizationContext capture that #3 added.
// bir2cir (SuspendColdLowering.AwaitResumeMethod) emits `this.intercepted().resumeWith(...)` in the
// OnCompleted callback; ContinuationImpl.intercepted() (stdlib) consults context[ContinuationInterceptor].
//
// Deterministic single-threaded drive: each awaited Task is a TaskCompletionSource NOT yet completed at the
// await (so the await genuinely SUSPENDS and registers OnCompleted), then completed synchronously via
// SetResult on the main thread with NO SynchronizationContext installed — the awaiter continuation runs
// INLINE (Codex-confirmed: default TCS + no SyncContext + single continuation + shallow stack).
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import kotlin.clr.await
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine
import dotkt.support.CountingInterceptor
import dotkt.support.Sink

// Named suspend funs so the await lives in a well-exercised named-fun state machine (mirrors il-taskawait).
suspend fun awaitCapturing(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await()
suspend fun awaitNoCapture(tcs: TaskCompletionSource1<Int>): Int = tcs.Task.await(captureContext = false)

// Scenario A: an interceptor is installed -> the AWAIT resume routes THROUGH the interceptor.
fun scenarioA() {
    val icept = CountingInterceptor()
    val sink = Sink<Int>(icept)
    val tcs = TaskCompletionSource1<Int>()
    val block: suspend () -> Int = { awaitCapturing(tcs) }
    block.startCoroutine(sink)          // runs to the await, which SUSPENDS (tcs not completed)
    icept.resumes = 0                   // reset: isolate the await-point resume from the start resume
    tcs.SetResult(42)                   // inline resume -> OnCompleted -> this.intercepted() -> wrapper (resumes++)
    println("A:resumes=" + icept.resumes + " done=" + sink.done + " value=" + sink.value)
}

// Scenario B: NO interceptor, captureContext=true (default) -> intercepted() is identity, the capturing
// awaiter resumes correctly (do not regress #3's default path over the SUSPEND path).
fun scenarioB() {
    val sink = Sink<Int>(EmptyCoroutineContext)
    val tcs = TaskCompletionSource1<Int>()
    val block: suspend () -> Int = { awaitCapturing(tcs) + 5 }
    block.startCoroutine(sink)
    tcs.SetResult(7)
    println("B:done=" + sink.done + " value=" + sink.value)
}

// Scenario C: NO interceptor, captureContext=false -> the ConfigureAwait(false) awaiter resumes inline.
fun scenarioC() {
    val sink = Sink<Int>(EmptyCoroutineContext)
    val tcs = TaskCompletionSource1<Int>()
    val block: suspend () -> Int = { awaitNoCapture(tcs) + 2 }
    block.startCoroutine(sink)
    tcs.SetResult(9)
    println("C:done=" + sink.done + " value=" + sink.value)
}

fun main() {
    scenarioA()   // A:resumes=1 done=True value=42
    scenarioB()   // B:done=True value=12
    scenarioC()   // C:done=True value=11
}
