package genericboundcapture

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

class Gate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun resume() { val saved = pending!!; pending = null; saved.resume(Unit) }
}
open class Box<T>(val items: List<T?>) {
    inline fun visit(block: () -> Unit) {
        check(items.size == 2)
        block()
    }
}
private class Completion<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var outcome: Result<T>? = null
    override fun resumeWith(result: Result<T>) { outcome = result }
}
private fun <T, U : Box<T>> capture(value: U): U {
    val current = value
    value.visit { if (value.items.size == 2) return current }
    return current
}
private fun <T, U : Box<T>> suspended(value: U, gate: Gate): suspend () -> U = {
    check(value.items.size == 2)
    gate.pause()
    check(value.items.size == 2)
    value
}
inline fun <T, U : Box<T>> spliced(value: U, gate: Gate): suspend () -> U = {
    check(value.items.size == 2)
    gate.pause()
    value
}
private class Host<X>(val marker: X) {
    fun <Unused, U : Box<X>> suspended(value: U, gate: Gate): suspend () -> U = {
        check(marker != null)
        check(value.items.size == 2)
        gate.pause()
        value
    }
}
private fun <U> complete(action: suspend () -> U, gate: Gate, expected: U) {
    val completion = Completion<U>()
    action.startCoroutine(completion)
    check(completion.outcome == null)
    gate.resume()
    check(completion.outcome!!.getOrThrow() === expected)
}

class GenericBoundCaptureTests {
    @TestAttribute
    fun inlineReceiverKeepsTheCallersTypeVariable() {
        val strings = Box<String>(listOf(null, "ok"))
        val integers = Box<Int>(listOf(null, 7))
        check(capture(strings) === strings)
        check(capture(integers) === integers)
    }

    @TestAttribute
    fun suspendLambdaUsesPhysicalBoundsAndRetainsMemberDispatch() {
        val strings = Box<String>(listOf(null, "ok"))
        val integers = Box<Int>(listOf(null, 7))
        val first = Gate()
        complete(suspended(strings, first), first, strings)
        val second = Gate()
        complete(suspended(integers, second), second, integers)
        val third = Gate()
        complete(Host("owner").suspended<Any, Box<String>>(strings, third), third, strings)
        val fourth = Gate()
        complete(spliced(integers, fourth), fourth, integers)
    }
}
