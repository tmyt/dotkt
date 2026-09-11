package localinheritedsuspend

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.clr.ClrName
import kotlin.coroutines.*

private class Gate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
}
private interface Slot<T> { suspend fun read(): T }
private open class Body<T>(private val gate: Gate<T>) {
    @ClrName("fetchSuspend")
    suspend fun read(): T = gate.await()
}
private class Derived<T>(gate: Gate<T>) : Body<T>(gate), Slot<T>
private interface MethodSlot { suspend fun <R> echo(gate: Gate<R>): R }
private open class MethodBody { suspend fun <R> echo(gate: Gate<R>): R = gate.await() }
private class DerivedMethod : MethodBody(), MethodSlot
private interface ArgumentSlot<T> { suspend fun read(value: T): T }
private open class ArgumentBody<T> { suspend fun read(value: T): T = value }
private class CapturedOwner<T> {
    fun make(): ArgumentSlot<T> {
        class Local<U> : ArgumentBody<T>(), ArgumentSlot<T>
        return Local<Int>()
    }
}
private open class VirtualBody(private val gate: Gate<String>) {
    open suspend fun read(): String = gate.await()
}
private open class VirtualMiddle(gate: Gate<String>) : VirtualBody(gate), Slot<String>
private class DerivedOverride(gate: Gate<String>) : VirtualMiddle(gate) {
    override suspend fun read(): String = super.read() + "!"
}

private class Completion<T> : Continuation<T> {
    var outcome: Result<T>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) { outcome = result }
}

private fun <T> verifySuspension(gate: Gate<T>, value: T, expected: T, action: suspend () -> T) {
    val completion = Completion<T>()
    action.startCoroutine(completion)
    assertTrue(completion.outcome == null)
    gate.resume(value)
    assertEquals(expected, completion.outcome!!.getOrThrow())
}

class InheritedSuspendTests {
    @TestAttribute
    fun capturedOwnerPermutationKeepsCalleeSignature() {
        val slot = CapturedOwner<String>().make()
        val completion = Completion<String>()
        val action: suspend () -> String = { slot.read("permuted") }
        action.startCoroutine(completion)
        assertEquals("permuted", completion.outcome!!.getOrThrow())
    }

    @TestAttribute
    fun genericOwnerRetainsRenamedSuspendSlots() {
        val text = Gate<String>()
        val textSlot: Slot<String> = Derived(text)
        verifySuspension(text, "text", "text") { textSlot.read() }
        val number = Gate<Int>()
        val numberSlot: Slot<Int> = Derived(number)
        verifySuspension(number, 42, 42) { numberSlot.read() }
        val nullable = Gate<Int?>()
        val nullableSlot: Slot<Int?> = Derived(nullable)
        verifySuspension(nullable, null, null) { nullableSlot.read() }
    }

    @TestAttribute
    fun methodGenericFrameSurvivesSuspension() {
        val slot: MethodSlot = DerivedMethod()
        val text = Gate<String>()
        verifySuspension(text, "text", "text") { slot.echo(text) }
        val number = Gate<Int>()
        verifySuspension(number, 42, 42) { slot.echo(number) }
    }

    @TestAttribute
    fun inheritedSlotKeepsVirtualDispatch() {
        val gate = Gate<String>()
        val slot: Slot<String> = DerivedOverride(gate)
        verifySuspension(gate, "base", "base!") { slot.read() }
        val baseGate = Gate<String>()
        val base: VirtualBody = DerivedOverride(baseGate)
        verifySuspension(baseGate, "base", "base!") { base.read() }
    }
}
