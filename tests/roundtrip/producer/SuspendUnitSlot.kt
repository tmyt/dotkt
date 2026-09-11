package roundtrip.suspendunitslot

import kotlin.coroutines.*
import kotlin.coroutines.cancellation.CancellationException

interface UnitSlot<T> { suspend fun read(): T }
interface PlainUnitSlot { suspend fun read() }
interface ErasedUnitSlot<T> { suspend fun read(value: T?): T }
interface NullableResultSlot<T> { suspend fun read(): T? }
interface MethodUnitSlot<T> { suspend fun <U : Any> read(value: U): T }
interface UnconstrainedMethodUnitSlot<T> { suspend fun <U> read(value: U): T }
abstract class UnitBaseSlot<T> { abstract suspend fun read(): T }
abstract class ErasedBaseUnitSlot<T> { abstract suspend fun read(value: T?): T }

class UnitGate {
    private var pending: Continuation<Unit>? = null
    var entries = 0
    suspend fun pause() {
        entries++
        suspendCoroutine<Unit> { pending = it }
    }
    fun release() { pending!!.resume(Unit) }
    fun fail() { pending!!.resumeWithException(IllegalStateException("unit slot failure")) }
    fun cancel() { pending!!.resumeWithException(CancellationException("unit slot cancellation")) }
}

class ImmediateUnitSlot : UnitSlot<Unit>, PlainUnitSlot {
    override suspend fun read() {}
}
open class DelayedUnitSlot(private val gate: UnitGate) : UnitSlot<Unit>, PlainUnitSlot {
    override open suspend fun read() { gate.pause() }
}
class FurtherUnitSlot(private val gate: UnitGate) : DelayedUnitSlot(gate) {
    var calls = 0
    override suspend fun read() { calls++; gate.pause() }
}
class DelayedBaseUnitSlot(private val gate: UnitGate) : UnitBaseSlot<Unit>() {
    override suspend fun read() { gate.pause() }
}
class ErasedParameterUnitSlot(private val gate: UnitGate) : ErasedUnitSlot<Unit> {
    var wasNull = false
    override suspend fun read(value: Unit?) { wasNull = value == null; gate.pause() }
}
class NullableErasedUnitSlot(private val gate: UnitGate) : NullableResultSlot<Unit> {
    override suspend fun read() { gate.pause() }
}
class GenericMethodUnitSlot(private val gate: UnitGate) : MethodUnitSlot<Unit> {
    override suspend fun <U : Any> read(value: U) { gate.pause() }
}

private var defaultCalls = 0
fun defaultCount(): Int = defaultCalls
interface DefaultUnitSlot : UnitSlot<Unit> {
    override suspend fun read() { defaultCalls++ }
}
class DefaultUnitBody : DefaultUnitSlot
open class DefaultUnitBase : DefaultUnitSlot
open class FinalUnitBodyBase { suspend fun read() {} }
class InheritedUnitBody : FinalUnitBodyBase(), UnitSlot<Unit>
abstract class AbstractUnitBody : UnitSlot<Unit> { abstract override suspend fun read() }
class ConcreteUnitBody : AbstractUnitBody() { override suspend fun read() {} }

open class OpenUnitBodyBase(private val gate: UnitGate) {
    open suspend fun read() { gate.pause() }
}
open class InheritedOpenUnitBody(gate: UnitGate) : OpenUnitBodyBase(gate), UnitSlot<Unit>
class FurtherInheritedUnitBody(private val gate: UnitGate) : InheritedOpenUnitBody(gate) {
    var calls = 0
    override suspend fun read() { calls++; gate.pause() }
}

// These calls are compiled beside the declarations, independently of the consumers' DLL imports.
suspend fun observeLocal(slot: UnitSlot<Unit>): Any? = slot.read()
suspend fun observeLocalBase(slot: UnitBaseSlot<Unit>): Any? = slot.read()
