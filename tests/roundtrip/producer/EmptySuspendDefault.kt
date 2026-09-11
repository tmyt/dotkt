package roundtrip.emptysuspenddefault

import kotlin.coroutines.*

interface EmptyDefault { suspend fun read() {} }
interface AbstractSlot { suspend fun read() }
interface GenericSlot<T> { suspend fun read(): T }
interface EmptyUnitDefault : GenericSlot<Unit> { override suspend fun read() {} }
open class EmptyBody : EmptyDefault
class EmptyGenericBody : EmptyUnitDefault
class AbstractBody : AbstractSlot { override suspend fun read() {} }

class DefaultGate {
    private var pending: Continuation<Unit>? = null
    var entries = 0
    suspend fun pause() { entries++; suspendCoroutine<Unit> { pending = it } }
    fun release() { pending!!.resume(Unit) }
}
interface SuspendingDefault {
    suspend fun read(gate: DefaultGate) { gate.pause() }
}
class SuspendingBody : SuspendingDefault

suspend fun localEmpty(): Any? = EmptyBody().read()
suspend fun localGeneric(): Any? = (EmptyGenericBody() as GenericSlot<Unit>).read()
suspend fun localAbstract(): Any? = (AbstractBody() as AbstractSlot).read()
