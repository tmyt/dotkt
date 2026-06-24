// T10 — finally around a suspension point. The finally body runs after the awaited try body completes (and, on the
// exception-unwind path, in a synthesized catch-that-rethrows) — but it is NOT a CLR finally clause, so a suspend
// (which `leave`s the .try to return from MoveNext) never runs it spuriously. v1: fall-through try body (a `return`
// inside the try skips the finally; both-catch-and-finally and a suspending finally are loud errors).
import clr.Api2
import clr.Task
import clr.Co
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun awaitInt(task: Task<Int>): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(task, c)
    COROUTINE_SUSPENDED
}

suspend fun work(): Int {
    var x = 0
    try {
        x = awaitInt(Api2.step(5))     // suspends here -> 5
        x = x + awaitInt(Api2.step(10)) // a second suspension inside the try -> 15
    } finally {
        println("cleanup")            // runs once, after the try body completes (not on either suspend)
    }
    return x                           // 15
}

fun main() {
    Co.runBlocking {
        println(work())                // cleanup / 15
        0
    }
}
