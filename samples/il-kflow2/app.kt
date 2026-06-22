// T11 — receiver-style suspend lambda: `flow { emit(x) }` (the block is `suspend FlowCol<T>.() -> Int`, and
// `emit` is a suspend EXTENSION on the implicit receiver). The idiomatic kotlinx form. Builds on il-dsl (receiver
// lambdas) + suspend-lambda CPS; sequence{} already proved receiver-style yield.
import clr.Task
import clr.FlowCol
import clr.Flow
import clr.Flows
import clr.Co
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun <T> FlowCol<T>.emit(value: T): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(this.emitRaw(value), c)
    COROUTINE_SUSPENDED
}
suspend fun <T> Flow<T>.collect(action: suspend (T) -> Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(Flows.collectRaw(this, action), c)
    COROUTINE_SUSPENDED
}

fun main() {
    Co.runBlocking {
        val f: Flow<String> = Flows.flow {   // receiver-style: emit on the implicit FlowCol<String>
            emit("a")
            emit("bb")
            emit("ccc")
            0
        }
        f.collect { v ->
            println(v.length)                 // 1 / 2 / 3
            0
        }
    }
}
