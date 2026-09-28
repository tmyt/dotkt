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
