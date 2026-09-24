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
private inline fun <T> copyThrough(value: T, block: (T) -> T): T = block(value)

class ProjectedListDefaultTests {
    @TestAttribute
    fun projectedDeclarationSlotsKeepValueCovarianceAndMutation() {
        val integers: List<Int> = listOf(1, 2)
        val strings: List<String> = listOf("a", "b")
        check(keepComparable(integers) === integers)
        check(keepComparable(strings) === strings)
        var projected: List<Comparable<*>> = strings
        check(keepComparable(projected) === strings)
        projected = integers
        check(keepComparable(projected) === integers)
        val mixed = mutableListOf(7, "x")
        check(mutateComparable(mixed) == 3)
        check(mixed[2] == 3)
        check((mixed as Any) is MutableList<*>)
        check((mixed as Any) as MutableList<Comparable<*>> === mixed)
    }

    @TestAttribute
    fun importedDefaultsKeepConstructedArgumentTemporaries() {
        check(start { nestedFirst() }.result!!.getOrThrow() == 7)
        check(start { directFirst() }.result!!.getOrThrow() == 7)
        check(start { listDefault(listOf<Any>(7, "x")) }.result!!.getOrThrow() == 7)
        var evaluations = 0
        val result = start {
            listDefault(run { evaluations++; listOf(7, "x") })
        }
        check(result.result!!.getOrThrow() == 7)
        check(evaluations == 1)
        check(start { listDefault(copyThrough(listOf(7, "x")) { it }) }.result!!.getOrThrow() == 7)
        check(start {
            listDefault(copyThrough(copyThrough(listOf(9, "y")) { it }) { it })
        }.result!!.getOrThrow() == 9)
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

        val argumentGate = ListDefaultGate()
        val afterArgument = start {
            listAfterToken(run { evaluations++; listOf(13, "z") }, argumentGate.token())
        }
        check(afterArgument.result == null)
        check(evaluations == 2)

        val copiedGate = ListDefaultGate()
        val copied = start {
            listAfterToken(copyThrough(listOf(17, "copy")) { it }, copiedGate.token())
        }
        check(copied.result == null)
        copiedGate.release()
        check(copied.result!!.getOrThrow() == 17)
        val innerGate = ListDefaultGate()
        val inner = start {
            listDefault(copyThrough(listOf(19, "inner")) { innerGate.pause(); it })
        }
        check(inner.result == null)
        innerGate.release()
        check(inner.result!!.getOrThrow() == 19)
        argumentGate.release()
        check(afterArgument.result!!.getOrThrow() == 13)
        check(evaluations == 2)
    }
}
