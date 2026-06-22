// T8 (bridge) — IAsyncEnumerable<T>.asFlow(): a .NET async stream (Kfc.Api.range) bridged into a Kotlin Flow and
// collected. Proves Flow <-> IAsyncEnumerable interop (the CLR-native multi-value bridge).
import clr.Task
import clr.AsyncSeq
import clr.FlowCol
import clr.Flow
import clr.Api
import clr.Flows
import clr.Co
import clr.Bridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

suspend fun <T> Flow<T>.collect(action: suspend (T) -> Int): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onComplete(Flows.collectRaw(this, action), c)
    COROUTINE_SUSPENDED
}

fun main() {
    Co.runBlocking {
        val f: Flow<Int> = Flows.fromAsync(Api.range(4))
        f.collect { v ->
            println(v * 10)        // 0 / 10 / 20 / 30
            0
        }
    }
}
