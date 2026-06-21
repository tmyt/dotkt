// Phase 5 (slice) — Flow on the foundation: a Flow wraps a `suspend (collector)->…` block; `collect` runs it;
// `emit` awaits the consumer action's Task (push-based, cold). No new state-machine form — pure suspend funs.
import clr.Task
import clr.FlowColI
import clr.FlowI
import clr.Flows
import clr.Co
import clr.KCont
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

@KCont suspend fun FlowColI.emit(value: Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(this.emitRaw(value), c)
    COROUTINE_SUSPENDED
}

@KCont suspend fun FlowI.collect(action: suspend (Int) -> Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(Flows.collectRaw(this, action), c)
    COROUTINE_SUSPENDED
}

fun main() {
    Co.runBlocking {
        val f = Flows.flow { col ->
            col.emit(1)
            col.emit(2)
            col.emit(3)
            0
        }
        f.collect { v ->
            println(v)
            v
        }
    }
}
