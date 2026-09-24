package roundtriptests.inheritedsuspendcovariance

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.inheritedsuspendcovariance.*
import GenericValueInterop.InheritedSuspendTaskApi

open class Consumed(gate: Gate) : Body(gate), Factory
class ConsumedGeneric(gate: Gate) : Body(gate), GenericFactory<Value>
class ConsumedOverride(gate: Gate) : Consumed(gate) {
    override suspend fun make(): Narrow = Narrow(super.make().text + "?")
}
class ImportedOverride(gate: Gate) : Produced(gate) {
    override suspend fun make(): Narrow = Narrow(super.make().text + "#")
}
private class Completion<T> : Continuation<T> {
    var outcome: Result<T>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) { outcome = result }
}
private fun verify(gate: Gate, expected: String, action: suspend () -> Value) {
    val completion = Completion<Value>()
    action.startCoroutine(completion)
    assertTrue(completion.outcome == null)
    gate.resume(Narrow("value"))
    assertEquals(expected, completion.outcome!!.getOrThrow().text)
}
class InheritedSuspendCovarianceTests {
    @TestAttribute
    fun constructedGenericInterfaceResumes() {
        val first = Gate()
        val produced: GenericFactory<Value> = ProducedGeneric(first)
        verify(first, "value") { produced.make() }
        val second = Gate()
        val consumed: GenericFactory<Value> = ConsumedGeneric(second)
        verify(second, "value") { consumed.make() }
    }
    @TestAttribute
    fun publicTaskSlotsResumeThroughInterface() {
        val first = Gate()
        assertEquals("task!", InheritedSuspendTaskApi.ThroughInterface(ProducedOverride(first), first))
        val second = Gate()
        assertEquals("task?", InheritedSuspendTaskApi.ThroughInterface(ConsumedOverride(second), second))
        val third = Gate()
        assertEquals("task#", InheritedSuspendTaskApi.ThroughInterface(ImportedOverride(third), third))
    }
    @TestAttribute
    fun publicTaskSlotPreservesBaseDispatch() {
        val gate = Gate()
        assertEquals("task#", InheritedSuspendTaskApi.ThroughBase(ImportedOverride(gate), gate))
    }
    @TestAttribute
    fun producedAndConsumedSlotsResume() {
        val first = Gate()
        val produced: Factory = Produced(first)
        verify(first, "value") { produced.make() }
        val second = Gate()
        val consumed: Factory = Consumed(second)
        verify(second, "value") { consumed.make() }
    }
    @TestAttribute
    fun furtherOverridesDispatchThroughInterface() {
        val first = Gate()
        val produced: Factory = ProducedOverride(first)
        verify(first, "value!") { produced.make() }
        val second = Gate()
        val consumed: Factory = ConsumedOverride(second)
        verify(second, "value?") { consumed.make() }
        val third = Gate()
        val imported: Factory = ImportedOverride(third)
        verify(third, "value#") { imported.make() }
    }
    @TestAttribute
    fun furtherOverridePreservesBaseDispatch() {
        val gate = Gate()
        val body: Body = ImportedOverride(gate)
        verify(gate, "value#") { body.make() }
    }
}
