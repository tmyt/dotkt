package roundtriptests.overloadedsuspendextensions

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.overloadedsuspendextensions.*

private class Completion : Continuation<String> {
    var done = false
    var value: String? = null
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<String>) {
        failure = result.exceptionOrNull()
        value = result.getOrNull()
        done = true
    }
    fun assertResult(expected: String) {
        check(done)
        failure?.let { throw it }
        check(value == expected)
    }
}

private suspend fun <T> forward(values: Collection<PendingValue<T>>): List<T> = values.awaitSelected()

class OverloadedSuspendExtensionTests {
    @TestAttribute
    fun importedCollectionJoinKeepsItsSelectedOverloadAcrossSuspensions() {
        val jobs = listOf(PendingJob(), PendingJob(), PendingJob())
        val completion = Completion()
        val run: suspend () -> String = { jobs.joinSelected() }
        run.startCoroutine(completion)
        for (job in jobs) {
            check(!completion.done)
            job.complete()
        }
        completion.assertResult("collection")
    }

    @TestAttribute
    fun importedArrayJoinKeepsItsDistinctSelectedOverloadAcrossSuspensions() {
        val jobs = arrayOf(PendingJob(), PendingJob(), PendingJob())
        val completion = Completion()
        val run: suspend () -> String = { joinSelected(*jobs) }
        run.startCoroutine(completion)
        for (job in jobs) {
            check(!completion.done)
            job.complete()
        }
        completion.assertResult("array")
    }

    @TestAttribute
    fun genericCollectionAwaitKeepsTheImportedColdEntryAndResultType() {
        val values = listOf(PendingValue("a"), PendingValue("b"), PendingValue("c"))
        val completion = Completion()
        val run: suspend () -> String = { forward(values).joinToString("|") }
        run.startCoroutine(completion)
        for (value in values) {
            check(!completion.done)
            value.complete()
        }
        completion.assertResult("a|b|c")
    }

    @TestAttribute
    fun genericArrayAwaitUsesTheOtherOverloadAndRetainsValueElements() {
        val values = arrayOf(PendingValue(1), PendingValue(2), PendingValue(3))
        val completion = Completion()
        val run: suspend () -> String = { awaitSelected(*values).joinToString("|") }
        run.startCoroutine(completion)
        for (i in values.indices.reversed()) {
            check(!completion.done)
            values[i].complete()
        }
        completion.assertResult("3|2|1")
    }
}
