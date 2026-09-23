package roundtrip.suspenddefaultframes

import kotlin.coroutines.*

suspend inline fun <reified T> firstSuspendDefault(
    item: Any,
    noinline block: suspend () -> T? = { if (item is T) item else null },
): T? = block()

suspend inline fun <T> wrapSuspendDefault(crossinline block: suspend () -> T): T {
    val materialized: suspend () -> T = { block() }
    return materialized()
}

class SuspendDefaultGate {
    private var pending: Continuation<Unit>? = null
    var entries = 0
    suspend fun pause() { entries++; suspendCoroutine<Unit> { pending = it } }
    fun release() { pending!!.resume(Unit) }
}

class SuspendDefaultOwner<T>(val value: T) {
    suspend fun read(block: suspend () -> T = { value }): T = block()
    suspend inline fun inlineRead(noinline block: suspend () -> T = { value }): T = block()
    suspend fun <M> method(item: M, block: suspend () -> M = { item }): M = block()
    suspend fun <M> both(item: M, block: suspend () -> Pair<T, M> = { Pair(value, item) }): Pair<T, M> = block()
    suspend fun delayed(gate: SuspendDefaultGate, block: suspend () -> T = { gate.pause(); value }): T = block()
    suspend fun nested(block: suspend () -> suspend () -> T = { { value } }): T = block()()
}
