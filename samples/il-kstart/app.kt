// T1 — startCoroutine: start a `suspend ()->T` block with a supplied Continuation<T> completion. Standalone:
// a runtime CaptureI completion observes the result (no dispatcher/runBlocking). The block really suspends.
import clr.Api2
import clr.Task
import clr.CaptureI
import clr.Bridge.onComplete
import kotlin.coroutines.startCoroutine
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun awaitInt(task: Task<Int>): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(task, c)
    COROUTINE_SUSPENDED
}
suspend fun produce(): Int {
    val a = awaitInt(Api2.step(40))
    return a + 2
}

fun main() {
    val sink = CaptureI()
    val block: suspend () -> Int = { produce() }
    block.startCoroutine(sink)
    println(sink.await())              // 42
}
