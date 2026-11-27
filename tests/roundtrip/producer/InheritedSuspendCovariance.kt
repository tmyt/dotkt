package roundtrip.inheritedsuspendcovariance

import kotlin.coroutines.*

open class OverloadedBase {
    suspend fun <M> pick(x: M): String = "generic"
    suspend fun <M> pick(x: String): String = x
}

open class Value(val text: String)
class Narrow(text: String) : Value(text)
open class WideClassFactory {
    open suspend fun make(): Value = Value("base")
    open suspend fun <T : Value> choose(value: T): Value = Value("base")
    open suspend fun label(present: Boolean): CharSequence? = "base"
}
class NarrowClassFactory(private val gate: Gate) : WideClassFactory() {
    override suspend fun make(): Narrow = gate.await()
    override suspend fun <T : Value> choose(value: T): T = value
    override suspend fun label(present: Boolean): StringBuilder? = if (present) StringBuilder("derived") else null
}
class Gate {
    private var pending: Continuation<Narrow>? = null
    suspend fun await(): Narrow = suspendCoroutine { pending = it }
    fun resume(value: Narrow) { pending!!.resume(value) }
}
interface Factory { suspend fun make(): Value }
interface GenericFactory<T> { suspend fun make(): T }
open class Body(val gate: Gate) {
    open suspend fun make(): Narrow = gate.await()
}
open class Produced(gate: Gate) : Body(gate), Factory
class ProducedGeneric(gate: Gate) : Body(gate), GenericFactory<Value>
class ProducedOverride(gate: Gate) : Produced(gate) {
    override suspend fun make(): Narrow = Narrow(super.make().text + "!")
}

class IntGate {
    private var pending: Continuation<Int>? = null
    suspend fun await(): Int = suspendCoroutine { pending = it }
    fun resume(value: Int) { pending!!.resume(value) }
}
interface NumericFactory { suspend fun make(delta: Int): Any }
open class NumericBody<T>(val tag: T, private val gate: IntGate) {
    open suspend fun make(delta: Int): Int = gate.await() + delta
}
class ProducedNumeric(gate: IntGate) : NumericBody<String>("producer", gate), NumericFactory

interface MethodFactory { suspend fun <T> echo(value: T): Any? }
open class MethodBody { open suspend fun <T> echo(value: T): T = value }
class ProducedMethod : MethodBody(), MethodFactory

interface WideResult<T> { suspend fun next(value: T): Any? }
open class GenericResult<T> : WideResult<T> {
    override suspend fun next(value: T): T = value
}
class ProducedIntResult(private val gate: IntGate) : GenericResult<Int>() {
    override suspend fun next(value: Int): Int = value + gate.await()
}
