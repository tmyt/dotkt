package localsuspenddefaultframe

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private suspend inline fun <reified T> localDefaultMatches(
    item: Any?,
    noinline block: suspend () -> Boolean = { item is T },
): Boolean = block()

private suspend inline fun <reified X, reified T> localDefaultForward(item: Any?): Boolean =
    localDefaultMatches<T>(item)

private suspend inline fun <reified A, reified B> localDefaultPair(
    item: Any?,
    noinline block: suspend () -> Boolean = { item is A && item is B },
): Boolean = block()

private suspend inline fun <reified X, reified T> localDefaultMerged(item: Any?): Boolean =
    localDefaultPair<T, T>(item)

private suspend inline fun <reified A, reified B> localDefaultReversed(item: Any?): Boolean =
    localDefaultPair<B, A>(item)

private suspend inline fun <reified T> localDefaultNested(
    item: Any?,
    noinline block: suspend () -> Boolean = { localDefaultMatches<T>(item) },
): Boolean = block()

private suspend inline fun <reified X, reified T> localDefaultNestedForward(item: Any?): Boolean =
    localDefaultNested<T>(item)

private inline fun <reified T> localDefaultOrdinary(
    item: Any?, noinline block: () -> Boolean = { item is T },
): Boolean = block()
private inline fun <reified X, reified T> localDefaultOrdinaryForward(item: Any?): Boolean =
    localDefaultOrdinary<T>(item)
private inline fun <reified T> localDefaultNonCapturing(noinline block: () -> Boolean = { null is T }): Boolean = block()
private inline fun <reified X, reified T> localDefaultNonCapturingForward(): Boolean = localDefaultNonCapturing<T>()

private fun interface LocalDefaultPredicate { fun matches(): Boolean }
private inline fun <reified T> localDefaultSam(
    item: Any?, predicate: LocalDefaultPredicate = LocalDefaultPredicate { item is T },
): Boolean = predicate.matches()
private inline fun <reified X, reified T> localDefaultSamForward(item: Any?): Boolean =
    localDefaultSam<T>(item)

private class LocalDefaultBox<T>(val value: T) {
    suspend fun read(block: suspend () -> T = { value }): T = block()
}
private suspend fun <X, T> localDefaultBoxForward(box: LocalDefaultBox<T>): T = box.read()

interface LocalDefaultBound<T> { fun value(): T }
private class LocalDefaultStringBound : LocalDefaultBound<String> { override fun value(): String = "bound" }
private class LocalDefaultIntBound : LocalDefaultBound<Int> { override fun value(): Int = 19 }
private class LocalDefaultOwner<T> {
    fun <U> pick(value: U, block: () -> U = { value }): U = block()
    fun <R : LocalDefaultBound<T>> forward(other: LocalDefaultOwner<String>, value: R): R =
        other.pick(value)
}
private fun <T> localDefaultChoose(value: T, block: () -> T = {
    fun <U : T> identity(item: U): U = item
    identity(value)
}): T = block()
private fun <X, T> localDefaultChooseForward(value: T): T = localDefaultChoose(value)
private suspend fun <T, U : LocalDefaultBound<T>> localDefaultBound(
    value: U, block: suspend () -> T = { value.value() },
): T = block()
private suspend fun <X, T, U : LocalDefaultBound<T>> localDefaultBoundForward(value: U): T =
    localDefaultBound<T, U>(value)

private class LocalDefaultGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause(): Unit = suspendCoroutine { pending = it }
    fun release() { val next = pending!!; pending = null; next.resume(Unit) }
}
private suspend inline fun <reified T> localDefaultDelayed(
    item: Any?, gate: LocalDefaultGate,
    noinline block: suspend () -> Boolean = { gate.pause(); item is T },
): Boolean = block()
private suspend inline fun <reified X, reified T> localDefaultDelayedForward(item: Any?, gate: LocalDefaultGate): Boolean =
    localDefaultDelayed<T>(item, gate)

private fun expectCompleted(expected: Boolean, block: suspend () -> Boolean) {
    var done = false
    var value = false
    var failure: Throwable? = null
    block.startCoroutine(object : Continuation<Boolean> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<Boolean>) {
            failure = result.exceptionOrNull()
            if (failure == null) value = result.getOrThrow()
            done = true
        }
    })
    check(done)
    failure?.let { throw it }
    check(value == expected)
}

class LocalSuspendDefaultFrameTests {
    @TestAttribute
    fun defaultLocalBoundsCloseButCallerBoundsStayInTheirOwnScope() {
        check(localDefaultChoose("local") == "local")
        check(localDefaultChoose(23) == 23)
        check(localDefaultChooseForward<Int, String>("generic") == "generic")
        check(localDefaultChooseForward<String, Int>(29) == 29)
        val value = LocalDefaultIntBound()
        check(LocalDefaultOwner<Int>().forward(LocalDefaultOwner<String>(), value) === value)
    }

    @TestAttribute
    fun defaultLambdaUsesItsCallersReorderedFrame() {
        expectCompleted(true) { localDefaultForward<Int, String?>(null) }
        expectCompleted(false) { localDefaultForward<Int, String>(null) }
        expectCompleted(true) { localDefaultForward<String, Int>(17) }
        expectCompleted(false) { localDefaultForward<Int, String>(17) }
    }

    @TestAttribute
    fun concreteMergedAndNestedDefaultFrames() {
        expectCompleted(true) { localDefaultMatches<String?>(null) }
        expectCompleted(false) { localDefaultMatches<String>(null) }
        expectCompleted(true) { localDefaultMerged<Int, String?>(null) }
        expectCompleted(false) { localDefaultMerged<Int, String>(null) }
        expectCompleted(true) { localDefaultReversed<String?, Any?>(null) }
        expectCompleted(false) { localDefaultReversed<String, Any?>(null) }
        expectCompleted(true) { localDefaultNestedForward<Int, String?>(null) }
        expectCompleted(false) { localDefaultNestedForward<Int, String>(null) }
    }

    @TestAttribute
    fun ordinarySamAndReceiverDefaultFrames() {
        check(localDefaultOrdinaryForward<Int, String?>(null))
        check(!localDefaultOrdinaryForward<Int, String>(null))
        check(localDefaultNonCapturingForward<Int, String?>())
        check(!localDefaultNonCapturingForward<Int, String>())
        check(localDefaultSamForward<Int, String?>(null))
        check(!localDefaultSamForward<Int, String>(null))
        expectCompleted(true) { localDefaultBoxForward<Int, String>(LocalDefaultBox("value")) == "value" }
        expectCompleted(true) { localDefaultBoxForward<String, Int>(LocalDefaultBox(31)) == 31 }
        val star: LocalDefaultBox<*> = LocalDefaultBox("star")
        expectCompleted(true) { star.read() == "star" }
        expectCompleted(true) {
            localDefaultBoundForward<Int, String, LocalDefaultStringBound>(LocalDefaultStringBound()) == "bound"
        }
    }

    @TestAttribute
    fun defaultFrameSurvivesActualSuspension() {
        val gate = LocalDefaultGate()
        var done = false
        var value = false
        var failure: Throwable? = null
        val block: suspend () -> Boolean = { localDefaultDelayedForward<Int, String?>(null, gate) }
        block.startCoroutine(object : Continuation<Boolean> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Boolean>) {
                failure = result.exceptionOrNull()
                if (failure == null) value = result.getOrThrow()
                done = true
            }
        })
        check(!done)
        gate.release()
        check(done)
        failure?.let { throw it }
        check(value)
    }
}
