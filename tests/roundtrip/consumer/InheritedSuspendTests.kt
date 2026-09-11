package roundtriptests.inheritedsuspend

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.inheritedsuspend.*

private class Consumed<T>(gate: Gate<T>) : Body<T>(gate), Slot<T>
private class ConsumedMethod : MethodBody(), MethodSlot
private class ConsumedOverride(gate: Gate<String>) : VirtualMiddle(gate) {
    override suspend fun read(): String = super.read() + "?"
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
    fun producedGenericOwnerRetainsRenamedSuspendSlots() {
        val text = Gate<String>()
        val textSlot: Slot<String> = Produced(text)
        verifySuspension(text, "text", "text") { textSlot.read() }
        val number = Gate<Int>()
        val numberSlot: Slot<Int> = Produced(number)
        verifySuspension(number, 42, 42) { numberSlot.read() }
    }

    @TestAttribute
    fun importedBaseRetainsRenamedSuspendSlots() {
        val text = Gate<String>()
        val textSlot: Slot<String> = Consumed(text)
        verifySuspension(text, "text", "text") { textSlot.read() }
        val number = Gate<Int>()
        val numberSlot: Slot<Int> = Consumed(number)
        verifySuspension(number, 42, 42) { numberSlot.read() }
    }

    @TestAttribute
    fun inheritedMethodGenericFrameSurvivesSuspension() {
        val producer: MethodSlot = ProducedMethod()
        val consumer: MethodSlot = ConsumedMethod()
        val text = Gate<String>()
        verifySuspension(text, "text", "text") { producer.echo(text) }
        val number = Gate<Int>()
        verifySuspension(number, 42, 42) { consumer.echo(number) }
    }

    @TestAttribute
    fun inheritedSuspendSlotKeepsVirtualDispatch() {
        val producerGate = Gate<String>()
        val producer: Slot<String> = ProducedOverride(producerGate)
        verifySuspension(producerGate, "base", "base!") { producer.read() }
        val consumerGate = Gate<String>()
        val consumer: Slot<String> = ConsumedOverride(consumerGate)
        verifySuspension(consumerGate, "base", "base?") { consumer.read() }
    }
}
