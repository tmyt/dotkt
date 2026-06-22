// T8 — Channel over System.Threading.Channels: suspend send/receive map to awaiting the Task forms. A bounded
// channel lets a single coroutine send N (capacity available -> completes) then receive N.
import clr.Task
import clr.Channel
import clr.Co
import clr.Bridge.onCompleteInt
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun <T> Channel<T>.send(v: T): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onCompleteInt(this.sendAsync(v), c)
    COROUTINE_SUSPENDED
}
suspend fun <T> Channel<T>.receive(): T = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(this.receiveAsync(), c)
    COROUTINE_SUSPENDED
}

fun main() {
    Co.runBlocking {
        val ch = Channel<Int>(10)
        ch.send(1)
        ch.send(2)
        ch.send(3)
        ch.close()
        val a = ch.receive()
        val b = ch.receive()
        val c = ch.receive()
        println(a + b + c)        // 6
        0
    }
}
