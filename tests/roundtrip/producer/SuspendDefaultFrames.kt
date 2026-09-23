package roundtrip.suspenddefaultframes

import kotlin.coroutines.*

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
