package genericboundcapture

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

class Gate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun resume() { val saved = pending!!; pending = null; saved.resume(Unit) }
}
open class Box<T>(val items: List<T?>) {
    inline fun outer(block: () -> Unit) { visit { block() } }
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
    value.outer { if (value.items.size == 2) return current }
    return current
}
private fun <T, U : Box<T>> nullableCapture(value: U?): U? {
    value?.outer { check(value.items.size == 2) }
    return value
}
internal open class PrivateNode<N : PrivateNode<N>>(val previous: N?) {
    private val count get() = 2
    inline fun visit(block: () -> Unit) { check(count == 2); block() }
}
internal open class PrivateSegment<S : PrivateSegment<S>> : PrivateNode<S>(null)
internal class PrivateConcrete : PrivateSegment<PrivateConcrete>()
private fun <S : PrivateSegment<S>> privateCapture(value: S): S { value.visit {}; return value }
private fun <T, U : Box<T>> suspended(value: U, gate: Gate): suspend () -> U = {
    check(value.items.size == 2)
    gate.pause()
    check(value.items.size == 2)
    value
}
private fun <T, U : Box<T>> nested(value: U, gate: Gate): suspend () -> U = {
    val inner: suspend () -> U = {
        gate.pause()
        check(value.items.size == 2)
        value
    }
    inner()
}
private interface Named { fun label(): String }
private class NamedBox<T>(items: List<T?>) : Box<T>(items), Named {
    override fun label() = "retained"
}
private fun <T, U> retained(value: U, gate: Gate): suspend () -> U where U : Box<T>, U : Named = {
    check(value.label() == "retained")
    gate.pause()
    check(value.items.size == 2)
    check(value.label() == "retained")
    value
}
inline fun <T, U : Box<T>> spliced(value: U, gate: Gate, before: () -> Unit): suspend () -> U {
    before()
    return {
        check(value.items.size == 2)
        gate.pause()
        value
    }
}
inline fun <T> deferred(value: T, before: () -> Unit): suspend () -> T {
    before()
    return { value }
}
private fun <Unused, A, B : A> dependency(value: B): suspend () -> B = deferred(value) {}
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
        check(nullableCapture(strings) === strings)
        check(nullableCapture<Int, Box<Int>>(null) == null)
        val inherited = PrivateConcrete()
        check(privateCapture(inherited) === inherited)
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
        complete(spliced(integers, fourth) {}, fourth, integers)
        val fifth = Gate()
        complete(nested(strings, fifth), fifth, strings)
        val sixth = Gate()
        val named = NamedBox<Int>(listOf(null, 11))
        complete(retained(named, sixth), sixth, named)
        val dependent = Completion<String>()
        dependency<Unit, Any, String>("closed").startCoroutine(dependent)
        check(dependent.outcome!!.getOrThrow() == "closed")
    }
}
