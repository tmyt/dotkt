package roundtrip.genericcaptureframes

import kotlin.coroutines.*

class BoundGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun release() { pending!!.resume(Unit) }
}
open class BoundBox<T>(val items: List<T?>) {
    inline fun visit(block: () -> Unit) { check(items.size == 2); block() }
}
inline fun <T, U : BoundBox<T>> deferredBound(value: U, gate: BoundGate, before: () -> Unit): suspend () -> U {
    before()
    return { check(value.items.size == 2); gate.pause(); check(value.items.size == 2); value }
}

inline fun <T, R> deferredPair(first: T, second: R, before: () -> Unit): suspend () -> Pair<T, R> {
    before()
    return { Pair(first, second) }
}
