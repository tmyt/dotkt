// GENERIC Flow<T> — exercises generic facade TYPES (FlowCol<T>/Flow<T>), generic facade methods (flow<T>/
// collectRaw<T>), generic suspend funs (emit<T>/collect<T>), and member access on a generic-instantiated
// receiver (FlowCol<T>.emitRaw). The Flow value type T is fully generic (here String).
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
        val f: Flow<String> = Flows.flow { col ->
            col.emit("a")
            col.emit("bb")
            col.emit("ccc")
            0
        }
        f.collect { v ->
            println(v.length)
            0
        }
    }
}
