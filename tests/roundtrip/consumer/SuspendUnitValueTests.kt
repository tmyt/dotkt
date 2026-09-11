package roundtriptests.suspendunitvalue

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.suspendunitvalue.*

private class Completion<T> : Continuation<T> {
    var completed = false
    var value: Any? = "pending"
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        completed = true
    }
}
private fun <T> start(block: suspend () -> T): Completion<T> {
    val completion = Completion<T>()
    block.startCoroutine(completion)
    return completion
}
private fun assertUnit(completion: Completion<*>) {
    assertTrue(completion.completed)
    assertTrue(completion.failure == null)
    assertTrue(completion.value === Unit)
}

class SuspendUnitValueTests {
    @TestAttribute
    fun importedDirectCompletionProducesUnit() {
        assertUnit(start<Any> { directUnit(false) })
        assertUnit(start<Any> { directUnit(true) })
    }

    @TestAttribute
    fun importedSuspensionCompletesWithUnitExactlyOnce() {
        val gate = UnitGate()
        val completion = start<Any> { delayedUnit(gate) }
        assertTrue(!completion.completed)
        assertEquals(1, gate.calls)
        gate.resume()
        assertUnit(completion)
        assertEquals(1, gate.calls)
    }

    @TestAttribute
    fun importedSuspendValueAndGenericInvocationKeepUnit() {
        val gate = UnitGate()
        val action = unitAction(gate)
        val completion = start<Any> { invokeAction(action) }
        assertTrue(!completion.completed)
        gate.resume()
        assertUnit(completion)
        assertEquals(1, gate.calls)
        assertUnit(start<Unit> { directUnit(true) })
    }

    @TestAttribute
    fun importedFailureDoesNotBecomeSuccessfulUnit() {
        val gate = UnitGate()
        val completion = start<Any> { delayedUnit(gate) }
        gate.fail()
        assertTrue(completion.completed)
        assertEquals("imported failure", completion.failure!!.message)
        assertEquals(1, gate.calls)
    }
}
