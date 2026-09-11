package roundtrip.inheritedsuspend

import kotlin.clr.ClrName
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class Gate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
}

interface Slot<T> { suspend fun read(): T }
open class Body<T>(private val gate: Gate<T>) {
    @ClrName("fetchSuspend")
    suspend fun read(): T = gate.await()
}
class Produced<T>(gate: Gate<T>) : Body<T>(gate), Slot<T>

interface MethodSlot { suspend fun <R> echo(gate: Gate<R>): R }
open class MethodBody { suspend fun <R> echo(gate: Gate<R>): R = gate.await() }
class ProducedMethod : MethodBody(), MethodSlot
inline fun producedInline(block: () -> Unit): MethodSlot {
    block()
    return object : MethodBody(), MethodSlot {}
}

interface UnitSlot { suspend fun complete(gate: Gate<Unit>) }
open class UnitBody { suspend fun complete(gate: Gate<Unit>) { gate.await() } }
class ProducedUnit : UnitBody(), UnitSlot

interface ExtensionSlot { suspend fun String.decorate(gate: Gate<String>): String }
open class ExtensionBody {
    suspend fun String.decorate(gate: Gate<String>): String = this + gate.await()
}
class ProducedExtension : ExtensionBody(), ExtensionSlot

interface ContextSlot {
    context(gate: Gate<String>)
    suspend fun readContext(): String
}
open class ContextBody {
    context(gate: Gate<String>)
    suspend fun readContext(): String = gate.await()
}
class ProducedContext : ContextBody(), ContextSlot

open class VirtualBody(private val gate: Gate<String>) {
    open suspend fun read(): String = gate.await()
}
open class VirtualMiddle(gate: Gate<String>) : VirtualBody(gate), Slot<String>
class ProducedOverride(gate: Gate<String>) : VirtualMiddle(gate) {
    override suspend fun read(): String = super.read() + "!"
}
