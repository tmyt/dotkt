package roundtriptests.inheritedsuspend

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.inheritedsuspend.*

private class Consumed<T>(gate: Gate<T>) : Body<T>(gate), Slot<T>
private class ConsumedMethod : MethodBody(), MethodSlot
private class ConsumedUnit : UnitBody(), UnitSlot
private class ConsumedExtension : ExtensionBody(), ExtensionSlot
private class ConsumedContext : ContextBody(), ContextSlot
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
    fun importedInlineFactoryKeepsInheritedSuspendSlots() {
        val slot = producedInline {}
        val gate = Gate<String>()
        verifySuspension(gate, "inline", "inline") { slot.echo(gate) }
    }

    @TestAttribute
    fun inheritedUnitMethodsSuspendBeforeReturning() {
        for (slot in listOf<UnitSlot>(ProducedUnit(), ConsumedUnit())) {
            val gate = Gate<Unit>()
            val completion = Completion<String>()
            val action: suspend () -> String = { slot.complete(gate); "done" }
            action.startCoroutine(completion)
            assertTrue(completion.outcome == null)
            gate.resume(Unit)
            assertEquals("done", completion.outcome!!.getOrThrow())
        }
    }

    @TestAttribute
    fun inheritedExtensionKeepsReceiverAcrossDll() {
        val producer = ProducedExtension()
        val producerGate = Gate<String>()
        verifySuspension(producerGate, "suffix", "prefixsuffix") {
            with(producer) { "prefix".decorate(producerGate) }
        }
        val consumer = ConsumedExtension()
        val consumerGate = Gate<String>()
        verifySuspension(consumerGate, "suffix", "prefixsuffix") {
            with(consumer) { "prefix".decorate(consumerGate) }
        }
    }

    @TestAttribute
    fun inheritedContextParameterKeepsRoleAcrossDll() {
        val producer = ProducedContext()
        val producerGate = Gate<String>()
        verifySuspension(producerGate, "producer", "producer") {
            with(producerGate) { producer.readContext() }
        }
        val consumer = ConsumedContext()
        val consumerGate = Gate<String>()
        verifySuspension(consumerGate, "consumer", "consumer") {
            with(consumerGate) { consumer.readContext() }
        }
    }

    @TestAttribute
    fun producedGenericOwnerRetainsRenamedSuspendSlots() {
        val text = Gate<String>()
        val textSlot: Slot<String> = Produced(text)
        verifySuspension(text, "text", "text") { textSlot.read() }
        val number = Gate<Int>()
        val numberSlot: Slot<Int> = Produced(number)
        verifySuspension(number, 42, 42) { numberSlot.read() }
        val nullable = Gate<Int?>()
        val nullableSlot: Slot<Int?> = Produced(nullable)
        verifySuspension(nullable, null, null) { nullableSlot.read() }
    }

    @TestAttribute
    fun importedBaseRetainsRenamedSuspendSlots() {
        val text = Gate<String>()
        val textSlot: Slot<String> = Consumed(text)
        verifySuspension(text, "text", "text") { textSlot.read() }
        val number = Gate<Int>()
        val numberSlot: Slot<Int> = Consumed(number)
        verifySuspension(number, 42, 42) { numberSlot.read() }
        val nullable = Gate<Int?>()
        val nullableSlot: Slot<Int?> = Consumed(nullable)
        verifySuspension(nullable, null, null) { nullableSlot.read() }
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
        val baseGate = Gate<String>()
        val base: VirtualBody = ConsumedOverride(baseGate)
        verifySuspension(baseGate, "base", "base?") { base.read() }
    }
}
