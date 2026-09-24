package roundtrip.projectedlistdefaults.tests

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.projectedlistdefaults.*

private class Completion : Continuation<Any?> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var result: Result<Any?>? = null
    override fun resumeWith(result: Result<Any?>) { this.result = result }
}

private fun start(block: suspend () -> Any?): Completion {
    val completion = Completion()
    block.startCoroutine(completion)
    return completion
}

private suspend fun invoke(block: suspend () -> Any?): Any? = block()
private suspend fun nestedFirst(): Any? = invoke { listDefault(listOf(7, "x")) }
private suspend fun directFirst(): Any? = listDefault(listOf(7, "x"))

class ProjectedListDefaultTests {
    @TestAttribute
    fun importedDefaultsKeepReifiableNestedArguments() {
        check(start { nestedFirst() }.result!!.getOrThrow() == 7)
        check(start { directFirst() }.result!!.getOrThrow() == 7)
        check(start { listDefault(listOf<Any>(7, "x")) }.result!!.getOrThrow() == 7)
        var evaluations = 0
        val result = start {
            listDefault(run { evaluations++; listOf(7, "x") })
        }
        check(result.result!!.getOrThrow() == 7)
        check(evaluations == 1)
        val rows = listOf(listOf(7, "x"))
        val returned = nestedLists(rows)
        check(returned === rows)
        check(returned[0][0] == 7)
        check(returned[0][1] == "x")
    }

    @TestAttribute
    fun importedDefaultCaptureSurvivesSuspension() {
        val gate = ListDefaultGate()
        var evaluations = 0
        val completion = start {
            invoke {
                delayedListDefault(run { evaluations++; listOf(11, "y") }, gate)
            }
        }
        check(completion.result == null)
        check(evaluations == 1)
        gate.release()
        check(completion.result!!.getOrThrow() == 11)
        check(evaluations == 1)
    }
}
