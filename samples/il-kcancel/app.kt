// T2 — suspendCancellableCoroutine: `await` written via the cancellation-aware leaf. The block gets a
// CancellableContinuation<T> (a Continuation<T>) and always suspends (no explicit COROUTINE_SUSPENDED).
import clr.Api2
import clr.Coro
import clr.Task
import clr.Bridge.onComplete
import kotlinx.coroutines.suspendCancellableCoroutine

suspend fun awaitC(task: Task<Int>): Int = suspendCancellableCoroutine { c ->
    onComplete(task, c)   // c : CancellableContinuation<Int> (a Continuation<Int>)
}

suspend fun total(): Int {
    val a = awaitC(Api2.step(10))
    val b = awaitC(Api2.step(20))
    return a + b           // 30
}

fun main() {
    println(Coro.run { total() })   // 30
}
