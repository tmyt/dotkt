package localcallresult

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private class Gate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
    fun release() { pending!!.resume(Unit) }
}

private class Completion : Continuation<Unit> {
    var done = false
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Unit>) {
        failure = result.exceptionOrNull()
        done = true
    }
}

private class Owner<A>(private val ownerValue: A) {
    fun <T> verifyGenericSelector(value: T) {
        val gate = Gate()
        var trace = ""
        fun <R> pick(item: R): suspend (T) -> Pair<R, T> {
            trace += "pick;"
            return { argument -> trace += "invoke;"; Pair(item, argument) }
        }
        suspend fun argument(): T { trace += "argument;"; gate.pause(); return value }
        val completion = Completion()
        val work: suspend () -> Unit = {
            val result = pick(ownerValue)(argument())
            check(result.first == ownerValue && result.second == value)
            trace += "done;"
        }
        work.startCoroutine(completion)
        check(!completion.done && trace == "pick;argument;")
        gate.release()
        completion.failure?.let { throw it }
        check(completion.done && trace == "pick;argument;invoke;done;")
    }

    fun <T> verifySequentialResults(value: T) {
        val gate = Gate()
        var trace = ""
        suspend fun <R> first(item: R): R { trace += "first;"; gate.pause(); return item }
        suspend fun second(): T { trace += "second;"; gate.pause(); return value }
        val completion = Completion()
        val work: suspend () -> Unit = {
            val result = Pair(first(ownerValue), second())
            check(result.first == ownerValue && result.second == value)
            trace += "done;"
        }
        work.startCoroutine(completion)
        check(!completion.done && trace == "first;")
        gate.release()
        check(!completion.done && trace == "first;second;")
        gate.release()
        completion.failure?.let { throw it }
        check(completion.done && trace == "first;second;done;")
    }

    fun <T> verify(value: T, nullable: Boolean) {
        val gate = Gate()
        var trace = ""
        fun selected(): suspend (T) -> Pair<A, T> {
            trace += "select;"
            return { item -> trace += "invoke;"; Pair(ownerValue, item) }
        }
        fun optional(): (suspend (T) -> Pair<A, T>)? = selected()
        suspend fun argument(): T { trace += "argument;"; gate.pause(); return value }
        val work: suspend () -> Unit = {
            val result = if (nullable) optional()!!(argument()) else selected()(argument())
            check(result.first == ownerValue)
            check(result.second == value)
            trace += "done;"
        }
        val completion = Completion()
        work.startCoroutine(completion)
        check(!completion.done)
        check(trace == "select;argument;")
        gate.release()
        check(completion.done)
        completion.failure?.let { throw it }
        check(trace == "select;argument;invoke;done;")
    }
}

class LocalCallResultTypeTests {
    @TestAttribute
    fun localGenericSelectorUsesItsInstantiatedResult() {
        Owner("owner").verifyGenericSelector(42)
        Owner(7).verifyGenericSelector("value")
    }

    @TestAttribute
    fun suspendedLocalResultSurvivesLaterSuspension() {
        Owner("owner").verifySequentialResults(42)
        Owner(7).verifySequentialResults("value")
    }

    @TestAttribute
    fun nonNullReceiverRetainsOwnerAndMethodFramesAcrossSuspension() {
        Owner("owner").verify(42, false)
        Owner(7).verify("value", false)
    }

    @TestAttribute
    fun nullableReceiverRetainsOwnerAndMethodFramesAcrossSuspension() {
        Owner("owner").verify(42, true)
        Owner(7).verify("value", true)
    }
}
