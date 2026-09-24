package roundtrip.genericcaptureframes.tests

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.genericcaptureframes.*

private class Completion<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var result: Result<T>? = null
    override fun resumeWith(result: Result<T>) { this.result = result }
}
private fun <T> complete(action: suspend () -> T): T {
    val completion = Completion<T>()
    action.startCoroutine(completion)
    return completion.result!!.getOrThrow()
}
private fun <Unused, A, B : A> reordered(first: A, second: B): suspend () -> Pair<B, A> =
    deferredPair(second, first) {}
private fun <Unused, A, B : A> duplicated(value: B): suspend () -> Pair<B, B> =
    deferredPair(value, value) {}
private fun <Unused, T, U : BoundBox<T>> bound(value: U, gate: BoundGate): suspend () -> U {
    value.visit { check(value.items.size == 2) }
    return deferredBound(value, gate) {}
}

class GenericCaptureFrameTests {
    @TestAttribute
    fun importedInlineCapturesCloseReorderedAndDuplicatedCallerFrames() {
        val reversed = complete(reordered<Unit, Any, String>(7, "value"))
        check(reversed.first == "value")
        check(reversed.second == 7)
        val repeated = complete(duplicated<Unit, Any, String>("same"))
        check(repeated.first == "same")
        check(repeated.second === repeated.first)
        val value = BoundBox<Int>(listOf(null, 9))
        val gate = BoundGate()
        val completion = Completion<BoundBox<Int>>()
        bound<Unit, Int, BoundBox<Int>>(value, gate).startCoroutine(completion)
        check(completion.result == null)
        gate.release()
        check(completion.result!!.getOrThrow() === value)
    }
}
