// Phase 4 capstone — structured concurrency on the foundation: `async` starts concurrent children, `await`
// (a suspend fun built from the raw intrinsic) joins them, `runBlocking` drives the root.
import clr.Api
import clr.Co
import clr.DeferredI
import clr.Task
import clr.KCont
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

@KCont suspend fun DeferredI.await(): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(this.task, c)
    COROUTINE_SUSPENDED
}
@KCont suspend fun Task<Int>.awaitI(): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(this, c)
    COROUTINE_SUSPENDED
}

fun main() {
    val sum = Co.runBlocking {
        val a = Co.async { Api.fetch(60, 10).awaitI() }
        val b = Co.async { Api.fetch(20, 20).awaitI() }
        a.await() + b.await()
    }
    println(sum)                       // 30
    val chained = Co.runBlocking {
        val x = Api.fetch(10, 7).awaitI()
        val y = Api.fetch(10, 35).awaitI()
        x + y
    }
    println(chained)                   // 42
}
