package roundtriptests.genericboundoverloads

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.genericboundoverloads.*

private class MemorySink<T> : Sink<T> { override fun send(value: T) {} }

private class Completion : Continuation<Unit> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var done = false
    var failure: Throwable? = null
    override fun resumeWith(result: Result<Unit>) {
        failure = result.exceptionOrNull()
        done = true
    }
    fun assertDone() {
        check(done)
        failure?.let { throw it }
    }
}

private fun <E : Any, C : Sink<E>> forward(source: Source<E?>, destination: C): C =
    source.copyTo(destination)

class GenericBoundOverloadTests {
    @TestAttribute
    fun importedBoundsSelectDistinctBodiesWithoutRenaming() {
        val source = Source<String?>(Pending())
        val collection = mutableListOf("collection")
        val sink = MemorySink<String>()
        check(source.copyTo(collection) === collection)
        check(source.copyTo(sink) === sink)
        check(source.collections == 1 && source.sinks == 1)
    }

    @TestAttribute
    fun localBoundsSelectDistinctBodiesWithoutRenaming() {
        check(localCallsKeepTheirBounds())
    }

    @TestAttribute
    fun genericForwardingKeepsTheSelectedBound() {
        val source = Source<Int?>(Pending())
        val sink = MemorySink<Int>()
        check(forward(source, sink) === sink)
        check(source.collections == 0 && source.sinks == 1)
    }

    @TestAttribute
    fun importedColdEntriesKeepDistinctBodiesAcrossSuspensions() {
        val pending = Pending()
        val source = Source<String?>(pending)
        val collection = mutableListOf("collection")
        val sink = MemorySink<String>()
        val completion = Completion()
        val run: suspend () -> Unit = {
            check(source.copyWaiting(collection) === collection)
            check(source.copyWaiting(sink) === sink)
        }
        run.startCoroutine(completion)
        check(!completion.done && source.collections == 0 && source.sinks == 0)
        pending.complete()
        check(!completion.done && source.collections == 1 && source.sinks == 0)
        pending.complete()
        completion.assertDone()
        check(source.collections == 1 && source.sinks == 1)
    }
}
