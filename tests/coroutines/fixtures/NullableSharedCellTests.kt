package nullablesharedcell

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private fun interface Sink<T> { suspend fun emit(value: T) }
private interface Source<T> { suspend fun collect(sink: Sink<T>) }
private suspend fun <T> Source<T>.lastValue(): T? {
    var result: T? = null
    collect(Sink { result = it })
    return result
}
private class Gate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun resume() { val saved = pending!!; pending = null; saved.resume(Unit) }
}
private class Values<T>(val values: List<T>, val gate: Gate?) : Source<T> {
    override suspend fun collect(sink: Sink<T>) {
        for (value in values) { gate?.pause(); sink.emit(value) }
    }
}
private class Completion<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var outcome: Result<T>? = null
    override fun resumeWith(result: Result<T>) { outcome = result }
}
private fun <T> verify(values: List<T>, expected: T?, suspended: Boolean) {
    val gate = if (suspended) Gate() else null
    val source: Source<T> = Values(values, gate)
    val completion = Completion<T?>()
    val action: suspend () -> T? = { source.lastValue() }
    action.startCoroutine(completion)
    if (gate != null) {
        for (ignored in values) { check(completion.outcome == null); gate.resume() }
    }
    check(completion.outcome != null)
    check(completion.outcome!!.getOrThrow() == expected)
}

class NullableSharedCellTests {
    @TestAttribute
    fun synchronousCaptureRetainsItsDeclaredFrame() {
        verify(listOf("first", "last"), "last", false)
        verify(listOf(1, 42), 42, false)
        verify(emptyList<String>(), null, false)
    }

    @TestAttribute
    fun suspendedCaptureRetainsItsDeclaredFrame() {
        verify(listOf("first", "last"), "last", true)
        verify(listOf(1, 42), 42, true)
        verify(listOf<Int?>(1, null), null, true)
    }
}
