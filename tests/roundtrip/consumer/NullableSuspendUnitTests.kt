package roundtriptests.nullablesuspendunit

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.nullablesuspendunit.*

private class Completion : Continuation<Unit?> {
    var completed = false
    var value: Any? = "pending"
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit?>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        completed = true
    }
}
private fun start(block: suspend () -> Unit?): Completion {
    val completion = Completion()
    block.startCoroutine(completion)
    return completion
}
private fun assertValue(completion: Completion, present: Boolean) {
    assertTrue(completion.completed)
    assertTrue(completion.failure == null)
    assertTrue(if (present) completion.value === Unit else completion.value == null)
}

class NullableSuspendUnitTests {
    @TestAttribute
    fun importedDirectAndImmediateResultsRemainNullable() {
        for (present in listOf(false, true)) {
            assertValue(start { directNullableUnit(present) }, present)
            assertValue(start { immediateNullableUnit(present) }, present)
        }
    }

    @TestAttribute
    fun importedInterfaceAndLocalForwarderPreserveResumedResults() {
        for (present in listOf(false, true)) {
            val gate = NullableUnitGate()
            val source: NullableUnitSource = gate
            val completion = start { source.read() }
            assertTrue(!completion.completed)
            gate.resume(present)
            assertValue(completion, present)
            val forwarded = start { localNullableUnit(source) }
            assertTrue(!forwarded.completed)
            gate.resume(present)
            assertValue(forwarded, present)
            val genericSource: GenericUnitSource<Unit?> = gate
            val generic = start { genericSource.read() }
            assertTrue(!generic.completed)
            gate.resume(present)
            assertValue(generic, present)
        }
    }

    @TestAttribute
    fun importedFailureAndCancellationStayFailures() {
        val directFailure = start { failNullableUnit() }
        assertTrue(directFailure.completed)
        assertEquals("nullable failure", directFailure.failure!!.message)
        val directCancellation = start { cancelNullableUnit() }
        assertTrue(directCancellation.completed)
        assertEquals("nullable cancellation", directCancellation.failure!!.message)
        val gate = NullableUnitGate()
        val failure = start { localNullableUnit(gate) }
        gate.fail()
        assertTrue(failure.completed)
        assertEquals("nullable failure", failure.failure!!.message)
        val cancellation = start { localNullableUnit(gate) }
        gate.cancel()
        assertTrue(cancellation.completed)
        assertEquals("nullable cancellation", cancellation.failure!!.message)
    }
}
