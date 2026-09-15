package roundtrip.nullablegenericsignature

import System.ArraySegment
import kotlin.coroutines.*

fun <T> echo(value: ArraySegment<T>?): ArraySegment<T>? = value

fun <T> choose(value: ArraySegment<T>?): Int = if (value == null) -1 else value.Count + 10
fun <T> choose(value: ArraySegment<T>): Int = value.Count

class SegmentEcho<T> {
    fun echo(value: ArraySegment<T>?): ArraySegment<T>? = value
    fun <U> other(value: ArraySegment<U>?): ArraySegment<U>? = value
    suspend fun suspendEcho(value: ArraySegment<T>?): ArraySegment<T>? = value
}

class SignatureGate {
    private var pending: Continuation<Unit>? = null
    suspend fun await(): Unit = suspendCoroutine { pending = it }
    fun complete() { pending!!.resume(Unit) }
}

suspend fun <T> delayedEcho(value: ArraySegment<T>?, gate: SignatureGate): ArraySegment<T>? {
    gate.await()
    return value
}
