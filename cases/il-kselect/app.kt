// T9 — select: race awaitables, resume with the handler result of whichever completes first (Task.WhenAny). The
// `select { onAwait(t){…} }` block is a receiver lambda registering clauses — no new compiler machinery (composes
// receiver lambdas + suspend handlers + generics + await). (The block's Int result is a dummy, like Flow.)
import clr.Task
import clr.Api
import clr.Selector
import clr.Sel
import clr.Co
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun <R> select(block: Selector<R>.() -> Int): R = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(Sel.selectAsync(block), c)
    COROUTINE_SUSPENDED
}

fun main() {
    Co.runBlocking {
        val r = select<Int> {
            onAwait(Api.delayed(1, 80)) { v -> v * 100 }     // slower (80ms)
            onAwait(Api.delayed(2, 10)) { v -> v * 1000 }    // faster (10ms) -> wins
            0
        }
        println(r)                                            // 2000
        0
    }
}
