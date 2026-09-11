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
private suspend fun observeDirect(early: Boolean): Any = directUnit(early)
private suspend fun observeDelayed(gate: UnitGate): Any = delayedUnit(gate)
private suspend fun observeFinally(gate: UnitGate): Any = finallyUnit(gate)
private suspend fun observeAction(action: suspend () -> Unit): Any = invokeAction(action)

class SuspendUnitValueTests {
    @TestAttribute
    fun importedEarlyReturnThroughSuspendingFinallyKeepsUnit() {
        val gate = UnitGate()
        val completion = start<Any> { observeFinally(gate) }
        assertTrue(!completion.completed)
        gate.resume()
        assertUnit(completion)
        assertEquals(1, gate.calls)
    }

    @TestAttribute
    fun importedDirectCompletionProducesUnit() {
        assertUnit(start<Any> { observeDirect(false) })
        assertUnit(start<Any> { observeDirect(true) })
    }

    @TestAttribute
    fun importedSuspensionCompletesWithUnitExactlyOnce() {
        val gate = UnitGate()
        val completion = start<Any> { observeDelayed(gate) }
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
        val completion = start<Any> { observeAction(action) }
        assertTrue(!completion.completed)
        gate.resume()
        assertUnit(completion)
        assertEquals(1, gate.calls)
        assertUnit(start<Unit> { directUnit(true) })
    }

    @TestAttribute
    fun importedFailureDoesNotBecomeSuccessfulUnit() {
        val gate = UnitGate()
        val completion = start<Any> { observeDelayed(gate) }
        gate.fail()
        assertTrue(completion.completed)
        assertEquals("imported failure", completion.failure!!.message)
        assertEquals(1, gate.calls)
        val pendingGate = UnitGate()
        val pendingReturn = start<Any> { observeFinally(pendingGate) }
        pendingGate.fail()
        assertTrue(pendingReturn.completed)
        assertEquals("imported failure", pendingReturn.failure!!.message)
    }
}
