package roundtriptests.suspenddefaultframes

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspenddefaultframes.*

private class FrameCompletion : Continuation<Any?> {
    var done = false
    var value: Any? = null
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        done = true
    }
}

private fun start(block: suspend () -> Any?): FrameCompletion {
    val completion = FrameCompletion()
    block.startCoroutine(completion)
    return completion
}

private fun completed(expected: Any?, block: suspend () -> Any?) {
    val completion = start(block)
    check(completion.done) { "did not complete: $expected" }
    val failure = completion.failure
    if (failure != null) throw failure
    check(completion.value == expected) { "expected $expected, got ${completion.value}" }
}

private class FrameCaller<A : CharSequence, B>(val label: A, val value: B) {
    suspend fun read(): B = SuspendDefaultOwner(value).read()
    suspend fun inlineRead(): B = SuspendDefaultOwner(value).inlineRead()
    suspend fun constructed(): List<B> = SuspendDefaultOwner(listOf(value)).read()
    suspend fun <M> method(item: M): M = SuspendDefaultOwner(value).method(item)
    suspend fun <X : CharSequence, M> reorderedMethod(marker: X, item: M): M = SuspendDefaultOwner(value).method(item)
    suspend fun <M> both(item: M): Pair<B, M> = SuspendDefaultOwner(value).both(item)
    suspend fun delayed(gate: SuspendDefaultGate): B = SuspendDefaultOwner(value).delayed(gate)
    suspend fun nested(): B = SuspendDefaultOwner(value).nested()
}

class SuspendDefaultFrameTests {
    @TestAttribute
    fun reorderedOwnerSlotsRetainValueAndReferenceTypes() {
        val integers = FrameCaller("owner", 17)
        val strings = FrameCaller("owner", "value")
        completed(17) { integers.read() }
        completed("value") { strings.read() }
        completed(17) { integers.inlineRead() }
        completed("value") { strings.inlineRead() }
    }

    @TestAttribute
    fun methodAndConstructedApplicationsKeepIndependentFrames() {
        val caller = FrameCaller("owner", 23)
        completed("method") { caller.method("method") }
        completed(31) { caller.method(31) }
        completed(37) { caller.reorderedMethod("method", 37) }
        completed("nested") { caller.reorderedMethod("method", listOf("nested"))[0] }
        completed(Pair(23, "both")) { caller.both("both") }
        completed(23) { caller.constructed()[0] }
    }

    @TestAttribute
    fun capturedOwnerSurvivesActualSuspension() {
        val gate = SuspendDefaultGate()
        val caller = FrameCaller("owner", 41)
        val completion = start { caller.delayed(gate) }
        check(!completion.done)
        check(gate.entries == 1)
        gate.release()
        check(completion.done)
        check(completion.failure == null)
        check(completion.value == 41)
    }

    @TestAttribute
    fun nestedSuspendDefaultsRetainTheirOwnFrames() {
        val caller = FrameCaller("owner", 53)
        completed(53) { caller.nested() }
    }
}
