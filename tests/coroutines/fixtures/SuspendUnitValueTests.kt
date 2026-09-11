package suspendunitvalue

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Threading.Tasks.Task
import kotlin.coroutines.*

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
private class Gate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
    fun fail(error: Throwable) { pending!!.resumeWithException(error) }
}
private class Trace { var text = "" }
private suspend fun effect(trace: Trace, early: Boolean) {
    trace.text += "effect;"
    if (early) return
    trace.text += "end;"
}
private suspend fun delayedEffect(gate: Gate<Unit>, trace: Trace) {
    try {
        trace.text += "before;"
        gate.await()
        trace.text += "after;"
    } finally { trace.text += "finally;" }
}
private suspend fun <T> invokeGeneric(block: suspend () -> T): T = block()
private suspend fun <T> echo(value: T): T = value
private suspend fun nullableUnit(): Unit? = null
private suspend fun returnThroughSuspendingFinally(gate: Gate<Unit>, synchronous: Boolean) {
    try { return } finally {
        if (synchronous) suspendCoroutine<Unit> { it.resume(Unit) } else gate.await()
    }
}
private suspend fun returnThroughPlainFinallyAfterSuspension(gate: Gate<Unit>, trace: Trace) {
    gate.await()
    try { return } finally { trace.text += "finally;" }
}
private suspend fun observeFinally(gate: Gate<Unit>, synchronous: Boolean): Any =
    returnThroughSuspendingFinally(gate, synchronous)
private suspend fun observePlainFinally(gate: Gate<Unit>, trace: Trace): Any =
    returnThroughPlainFinallyAfterSuspension(gate, trace)
private suspend fun observeEffect(trace: Trace, early: Boolean): Any = effect(trace, early)
private suspend fun observeDelayed(gate: Gate<Unit>, trace: Trace): Any = delayedEffect(gate, trace)
private suspend fun observeAction(action: suspend () -> Unit): Any = invokeGeneric(action)
private suspend fun returnFromExpression(gate: Gate<Unit>, early: Boolean, trace: Trace) {
    val value = if (early) return else gate.await()
    trace.text += "after;"
}
private suspend fun observeExpression(gate: Gate<Unit>, early: Boolean, trace: Trace): Any =
    returnFromExpression(gate, early, trace)
private fun consume(first: Any?, second: Any?): Any? = first
private fun second(trace: Trace): String { trace.text += "second;"; return "second" }
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
    fun expressionPositionEarlyReturnProducesUnit() {
        val gate = Gate<Unit>()
        val trace = Trace()
        assertUnit(start<Any> { observeExpression(gate, true, trace) })
        assertEquals("", trace.text)
        val completion = start<Any> { observeExpression(gate, false, trace) }
        assertTrue(!completion.completed)
        gate.resume(Unit)
        assertUnit(completion)
        assertEquals("after;", trace.text)
    }

    @TestAttribute
    fun earlyReturnThroughSuspendingFinallyProducesUnit() {
        val gate = Gate<Unit>()
        assertUnit(start<Any> { observeFinally(gate, true) })
        val completion = start<Any> { observeFinally(gate, false) }
        assertTrue(!completion.completed)
        gate.resume(Unit)
        assertUnit(completion)
    }

    @TestAttribute
    fun earlyReturnThroughPlainFinallyAfterSuspensionProducesUnit() {
        val gate = Gate<Unit>()
        val trace = Trace()
        val completion = start<Any> { observePlainFinally(gate, trace) }
        assertTrue(!completion.completed)
        gate.resume(Unit)
        assertEquals("finally;", trace.text)
        assertUnit(completion)
    }

    @TestAttribute
    fun directCompletionAndBareReturnProduceUnitValues() {
        val trace = Trace()
        assertUnit(start<Any> { observeEffect(trace, false) })
        assertEquals("effect;end;", trace.text)
        trace.text = ""
        assertUnit(start<Any> { observeEffect(trace, true) })
        assertEquals("effect;", trace.text)
    }

    @TestAttribute
    fun actualSuspensionCompletesWithUnit() {
        val trace = Trace()
        val gate = Gate<Unit>()
        val completion = start<Any> { observeDelayed(gate, trace) }
        assertTrue(!completion.completed)
        assertEquals("before;", trace.text)
        gate.resume(Unit)
        assertUnit(completion)
        assertEquals("before;after;finally;", trace.text)
    }

    @TestAttribute
    fun typedUnitCompletionAndSynchronousResumeKeepTheSingleton() {
        assertUnit(start<Unit> { })
        assertUnit(start<Unit> { suspendCoroutine<Unit> { it.resume(Unit) } })
        val trace = Trace()
        assertUnit(start<Unit> { effect(trace, true) })
    }

    @TestAttribute
    fun suspendFunctionValuesAndGenericConsumersKeepUnit() {
        val trace = Trace()
        val direct: suspend () -> Unit = { effect(trace, false) }
        assertUnit(start<Any> { observeAction(direct) })
        val gate = Gate<Unit>()
        val delayed: suspend () -> Unit = { delayedEffect(gate, trace) }
        val completion = start<Any> { observeAction(delayed) }
        assertTrue(!completion.completed)
        gate.resume(Unit)
        assertUnit(completion)
    }

    @TestAttribute
    fun consumedSuspendArgumentsEvaluateOnceInOrder() {
        val trace = Trace()
        val gate = Gate<Unit>()
        val completion = start<Any?> { consume(delayedEffect(gate, trace), second(trace)) }
        assertEquals("before;", trace.text)
        assertTrue(!completion.completed)
        gate.resume(Unit)
        assertUnit(completion)
        assertEquals("before;after;finally;second;", trace.text)
    }

    @TestAttribute
    fun failuresStayFailuresAndDoNotEvaluateLaterArguments() {
        val trace = Trace()
        val gate = Gate<Unit>()
        val failure = IllegalStateException("unit failure")
        val completion = start<Any?> { consume(delayedEffect(gate, trace), second(trace)) }
        gate.fail(failure)
        assertTrue(completion.completed)
        assertTrue(completion.failure === failure)
        assertEquals("before;finally;", trace.text)
        val immediate = start<Unit> { throw failure }
        assertTrue(immediate.failure === failure)
    }

    @TestAttribute
    fun nullableGenericCompletionRemainsNull() {
        val completion = start<Any?> { echo<Unit?>(null) }
        assertTrue(completion.completed)
        assertTrue(completion.failure == null)
        assertTrue(completion.value == null)
        val direct = start<Any?> { nullableUnit() }
        assertTrue(direct.completed)
        assertTrue(direct.failure == null)
        assertTrue(direct.value == null)
    }

    @TestAttribute
    fun voidTaskAwaitProducesUnitWhenConsumed() {
        assertUnit(start<Any> { Task.Delay(0).await() })
    }
}
