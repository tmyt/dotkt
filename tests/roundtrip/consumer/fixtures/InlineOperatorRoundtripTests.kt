import NUnit.Framework.TestAttribute
import roundtrip.inlineoperators.InlineOperator

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
    }
}
