package roundtriptests.emptysuspenddefault

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.emptysuspenddefault.*

private class Completion : Continuation<Any?> {
    var done = false
    var value: Any? = "pending"
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        done = true
    }
}
private fun start(block: suspend () -> Any?): Completion {
    val result = Completion()
    block.startCoroutine(result)
    return result
}
private fun assertUnit(result: Completion) {
    assertTrue(result.done)
    assertTrue(result.failure == null)
    assertTrue(result.value === Unit)
}
private suspend fun observe(source: EmptyDefault): Any? = source.read()
private suspend fun observeGeneric(source: GenericSlot<Unit>): Any? = source.read()
private class ImportedEmpty : EmptyDefault
private class ImportedGeneric : EmptyUnitDefault
private class ImportedAbstract : AbstractSlot { override suspend fun read() {} }
private class ImportedSuspending : SuspendingDefault
private class OverriddenEmpty(private val gate: DefaultGate) : EmptyBody() {
    override suspend fun read() { gate.pause() }
}

class EmptySuspendDefaultTests {
    @TestAttribute
    fun emptyAndAbstractDeclarationsRemainDistinctAcrossDlls() {
        assertUnit(start { localEmpty() })
        assertUnit(start { localGeneric() })
        assertUnit(start { localAbstract() })
        assertUnit(start { observe(EmptyBody()) })
        assertUnit(start { observe(ImportedEmpty()) })
        assertUnit(start { observeGeneric(EmptyGenericBody()) })
        assertUnit(start { observeGeneric(ImportedGeneric()) })
        assertUnit(start { (ImportedAbstract() as AbstractSlot).read() })
    }

    @TestAttribute
    fun nonemptyDefaultsAndOverridesActuallySuspend() {
        val gate = DefaultGate()
        val defaultResult = start { (ImportedSuspending() as SuspendingDefault).read(gate) }
        assertTrue(!defaultResult.done)
        assertEquals(1, gate.entries)
        gate.release()
        assertUnit(defaultResult)
        val overrideGate = DefaultGate()
        val overridden = start { observe(OverriddenEmpty(overrideGate)) }
        assertTrue(!overridden.done)
        assertEquals(1, overrideGate.entries)
        overrideGate.release()
        assertUnit(overridden)
    }
}
