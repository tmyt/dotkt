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

private class TextOrder : Comparable<String> {
    override fun compareTo(other: String): Int = 1
}
private enum class ProbeEnum { ONE }

class ProjectedListDefaultTests {
    @TestAttribute
    fun foreignInvariantOwnersDistinguishConsumedAndReifiedProjections() {
        val comparable = System.Collections.Generic.List<Comparable<*>>()
        comparable.Add("seven")
        check(readComparable(comparable) == "seven")
        val reified: System.Collections.Generic.List<Comparable<in String>> =
            System.Collections.Generic.List<Comparable<String>>()
        reified.Add(TextOrder())
        check(compareProjectedString(reified) == 1)
        val enums = System.Collections.Generic.List<Enum<*>?>()
        enums.Add(ProbeEnum.ONE)
        check(readEnum(enums)?.name == "ONE")
        enums[0] = null
        check(readEnum(enums) == null)
        val array = arrayOf<Comparable<*>>(7, "x")
        val arrays = System.Collections.Generic.List<Array<Comparable<*>>>()
        arrays.Add(array)
        check(readComparableArray(arrays) === array)
        val functions = System.Collections.Generic.List<() -> Comparable<*>>()
        functions.Add { "callable" }
        check(invokeComparable(functions) == "callable")
    }

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
