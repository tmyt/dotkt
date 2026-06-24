// Phase 4a — GENERIC suspend functions: the state machine is a generic TYPE over the fun's type params.
// Upstream kotlinx is pervasively generic (suspendCancellableCoroutine<T>, Deferred<T>.await(), withContext<T>).
import clr.Api2
import clr.Coro
import clr.Task
import clr.await

// Generic suspend fun: T is reified through a generic state-machine class.
suspend fun <T> awaitTwice(a: Task<T>, b: Task<T>): T {
    val x = a.await()
    b.await()
    return x
}

// Generic + transform: returns the second value.
suspend fun <T> second(a: Task<T>, b: Task<T>): T {
    a.await()
    return b.await()
}

fun main() {
    println(Coro.run { awaitTwice(Api2.step(7), Api2.step(99)) })   // 7  (T=Int)
    println(Coro.runS { awaitTwice(Api2.word("hi"), Api2.word("x")) }) // hi (T=String)
    println(Coro.run { second(Api2.step(1), Api2.step(2)) })        // 2
    println(Coro.runS { second(Api2.word("a"), Api2.word("b")) })   // b
}
