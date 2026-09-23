import NUnit.Framework.TestAttribute
import roundtrip.inlineoperators.InlineOperator
import roundtrip.inlineoperators.InlineDefaultOperator
import roundtrip.inlineoperators.InlineCrossOperator
import kotlin.coroutines.*

private class InheritedInlineOperator<T>(value: T) : InlineOperator<T>(value)

private fun importedOperatorReturn(): Int {
    val grid = InlineOperator(0)
    grid[{ return 41 }]
    return 99
}

private fun inheritedOperatorReturn(): String {
    val grid = InheritedInlineOperator("inherited")
    grid[3, { return it }]
    return "missed"
}

private var inlineOperatorTrace = ""
private var inlineOperatorContinuation: Continuation<Int>? = null

private suspend fun suspendInsideOperator(): Int = suspendCoroutine {
    inlineOperatorContinuation = it
}

private fun operatorReceiver(): InlineOperator<Int> {
    inlineOperatorTrace += "R"
    return InlineOperator(23)
}

private fun operatorIndex(): Int {
    inlineOperatorTrace += "I"
    return 7
}

private fun importedSetterReturn(): Int {
    try {
        operatorReceiver()[operatorIndex()] = {
            inlineOperatorTrace += "B"
            return it
        }
    } finally {
        inlineOperatorTrace += "F"
    }
    return 99
}

class InlineOperatorRoundtripTests {
    @TestAttribute
    fun importedAndInheritedOperatorsPreserveNonLocalReturn() {
        check(importedOperatorReturn() == 41)
        check(inheritedOperatorReturn() == "inherited")
    }

    @TestAttribute
    fun importedSetterPreservesEvaluationAndFinally() {
        inlineOperatorTrace = ""
        check(importedSetterReturn() == 23)
        check(inlineOperatorTrace == "RIBF")
    }

    @TestAttribute
    fun importedOperatorLambdaModifiersPreserveValues() {
        val grid = InlineOperator(7)
        var calls = 0
        check(grid[{ calls++; 11 }, "crossinline"] == 11)
        check(grid[{ calls++; 13 }, true] == 13)
        check(grid[5, { calls++; it + 10 }] == 17)
        check(calls == 3)
        check(InlineDefaultOperator()[{ calls++; 5 }] == 22)
        check(InlineCrossOperator()[{ calls++; 29 }] == 29)
        check(calls == 5)
    }

    @TestAttribute
    fun importedOperatorPreservesSuspensionAndResumption() {
        inlineOperatorContinuation = null
        var actual = 0
        var completed = false
        val operation: suspend () -> Int = {
            InlineOperator(0)[{ suspendInsideOperator() + 1 }]
        }
        operation.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                actual = result.getOrThrow()
                completed = true
            }
        })
        check(!completed)
        val continuation = inlineOperatorContinuation ?: error("did not suspend")
        inlineOperatorContinuation = null
        continuation.resume(37)
        check(completed && actual == 38)
    }
}
