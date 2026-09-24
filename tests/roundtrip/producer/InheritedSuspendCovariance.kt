package roundtrip.inheritedsuspendcovariance

import kotlin.coroutines.*

open class Value(val text: String)
class Narrow(text: String) : Value(text)
class Gate {
    private var pending: Continuation<Narrow>? = null
    suspend fun await(): Narrow = suspendCoroutine { pending = it }
    fun resume(value: Narrow) { pending!!.resume(value) }
}
interface Factory { suspend fun make(): Value }
open class Body(val gate: Gate) {
    open suspend fun make(): Narrow = gate.await()
}
open class Produced(gate: Gate) : Body(gate), Factory
class ProducedOverride(gate: Gate) : Produced(gate) {
    override suspend fun make(): Narrow = Narrow(super.make().text + "!")
}
