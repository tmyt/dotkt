package roundtrip.suspendunitvalue

import kotlin.coroutines.*

class UnitGate {
    private var pending: Continuation<Unit>? = null
    var calls = 0
    suspend fun await() {
        calls++
        suspendCoroutine<Unit> { pending = it }
    }
    fun resume() { pending!!.resume(Unit) }
    fun fail() { pending!!.resumeWithException(IllegalStateException("imported failure")) }
}
suspend fun directUnit(early: Boolean) { if (early) return }
suspend fun delayedUnit(gate: UnitGate) { gate.await() }
suspend fun finallyUnit(gate: UnitGate) { try { return } finally { gate.await() } }
fun unitAction(gate: UnitGate): suspend () -> Unit = { delayedUnit(gate) }
suspend fun <T> invokeAction(action: suspend () -> T): T = action()
