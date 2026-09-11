package roundtriptests.suspendunitslot

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.suspendunitslot.*

private class Completion : Continuation<Any?> {
    var completed = false
    var value: Any? = "pending"
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        completed = true
    }
}
private fun start(block: suspend () -> Any?): Completion {
    val completion = Completion()
    block.startCoroutine(completion)
    return completion
}
private fun assertUnit(completion: Completion) {
    assertTrue(completion.completed)
    assertTrue(completion.failure == null)
    assertTrue(completion.value === Unit)
}
private fun complete(gate: UnitGate, completion: Completion) {
    assertTrue(!completion.completed)
    assertEquals(1, gate.entries)
    gate.release()
    assertUnit(completion)
    assertEquals(1, gate.entries)
}
private suspend fun observe(slot: UnitSlot<Unit>): Any? = slot.read()
private suspend fun observeBase(slot: UnitBaseSlot<Unit>): Any? = slot.read()
private suspend fun observeErased(slot: ErasedUnitSlot<Unit>, value: Unit?): Any? = slot.read(value)
private suspend fun observeNullable(slot: NullableResultSlot<Unit>): Any? = slot.read()
private suspend fun observeMethod(slot: MethodUnitSlot<Unit>): Any? = slot.read("method frame")

private class ImportedInterfaceBody(private val gate: UnitGate) : UnitSlot<Unit>, PlainUnitSlot {
    override suspend fun read() { gate.pause() }
}
private class ImportedBaseBody(private val gate: UnitGate) : UnitBaseSlot<Unit>() {
    override suspend fun read() { gate.pause() }
}
private class ImportedErasedBody(private val gate: UnitGate) : ErasedUnitSlot<Unit> {
    override suspend fun read(value: Unit?) { gate.pause() }
}
private class ImportedFurtherBody(private val gate: UnitGate) : DelayedUnitSlot(gate) {
    var calls = 0
    override suspend fun read() { calls++; gate.pause() }
}

class SuspendUnitSlotTests {
    @TestAttribute
    fun directDefaultInheritedAndAbstractImplementationsReturnUnit() {
        assertUnit(start { observe(ImmediateUnitSlot()) })
        assertUnit(start { observeLocal(ImmediateUnitSlot()) })
        assertUnit(start { observe(InheritedUnitBody()) })
        assertUnit(start { observe(ConcreteUnitBody()) })
        val before = defaultCount()
        assertUnit(start { observe(DefaultUnitBody()) })
        assertEquals(before + 1, defaultCount())
    }

    @TestAttribute
    fun localAndImportedCallsActuallySuspendAndResume() {
        val interfaceGate = UnitGate()
        complete(interfaceGate, start { observe(DelayedUnitSlot(interfaceGate)) })
        val localGate = UnitGate()
        complete(localGate, start { observeLocal(DelayedUnitSlot(localGate)) })
        val baseGate = UnitGate()
        complete(baseGate, start { observeBase(DelayedBaseUnitSlot(baseGate)) })
        val localBaseGate = UnitGate()
        complete(localBaseGate, start { observeLocalBase(DelayedBaseUnitSlot(localBaseGate)) })
        val methodGate = UnitGate()
        complete(methodGate, start { observeMethod(GenericMethodUnitSlot(methodGate)) })
        val nullableGate = UnitGate()
        complete(nullableGate, start { observeNullable(NullableErasedUnitSlot(nullableGate)) })
        val erasedGate = UnitGate()
        val erased = ErasedParameterUnitSlot(erasedGate)
        complete(erasedGate, start { observeErased(erased, null) })
        assertTrue(erased.wasNull)
    }

    @TestAttribute
    fun separatelyCompiledImplementationsFillReferencedSlots() {
        val interfaceGate = UnitGate()
        complete(interfaceGate, start { observe(ImportedInterfaceBody(interfaceGate)) })
        val baseGate = UnitGate()
        complete(baseGate, start { observeBase(ImportedBaseBody(baseGate)) })
        val erasedGate = UnitGate()
        complete(erasedGate, start { observeErased(ImportedErasedBody(erasedGate), null) })
        val furtherGate = UnitGate()
        val further = ImportedFurtherBody(furtherGate)
        complete(furtherGate, start { observe(further) })
        assertEquals(1, further.calls)
    }

    @TestAttribute
    fun resumedFailuresAndCancellationStayFailures() {
        val failureGate = UnitGate()
        val failure = start { observe(DelayedUnitSlot(failureGate)) }
        assertTrue(!failure.completed)
        failureGate.fail()
        assertTrue(failure.completed)
        assertEquals("unit slot failure", failure.failure!!.message)
        val cancelGate = UnitGate()
        val cancellation = start { observe(ImportedInterfaceBody(cancelGate)) }
        assertTrue(!cancellation.completed)
        cancelGate.cancel()
        assertTrue(cancellation.completed)
        assertEquals("unit slot cancellation", cancellation.failure!!.message)
    }
}
