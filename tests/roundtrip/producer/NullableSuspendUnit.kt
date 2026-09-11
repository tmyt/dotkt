package roundtrip.nullablesuspendunit

import kotlin.coroutines.*
import kotlin.coroutines.cancellation.CancellationException

interface NullableUnitSource {
    suspend fun read(): Unit?
}

interface GenericUnitSource<T> {
    suspend fun read(): T
}

class NullableUnitGate : NullableUnitSource, GenericUnitSource<Unit?> {
    private var pending: Continuation<Unit?>? = null
    override suspend fun read(): Unit? = suspendCoroutine<Unit?> { pending = it }
    fun resume(present: Boolean) { pending!!.resume(if (present) Unit else null) }
    fun fail() { pending!!.resumeWithException(IllegalStateException("nullable failure")) }
    fun cancel() { pending!!.resumeWithException(CancellationException("nullable cancellation")) }
}

suspend fun directNullableUnit(present: Boolean): Unit? = if (present) Unit else null
suspend fun immediateNullableUnit(present: Boolean): Unit? =
    suspendCoroutine<Unit?> { it.resume(if (present) Unit else null) }
suspend fun localNullableUnit(source: NullableUnitSource): Unit? = source.read()
suspend fun ordinaryUnit() {}
suspend fun failNullableUnit(): Unit? = throw IllegalStateException("nullable failure")
suspend fun cancelNullableUnit(): Unit? = throw CancellationException("nullable cancellation")
