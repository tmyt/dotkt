import NUnit.Framework.TestAttribute
import bottominline.*

private fun bottomText(code: Int): String = bottomDispatch(code,
    { return "first" }, { return "second" })
private fun <T> bottomGeneric(value: T): BottomBox<T> = bottomForward(0, { return BottomBox(value) })
private fun bottomDefaultThrows(): String = bottomForward(1, { return "unused" })
private var bottomEvaluationCount = 0
private fun bottomCode(): Int { bottomEvaluationCount++; return 0 }
private fun bottomEvaluated(): String = bottomDispatch(bottomCode(), {
    bottomEvaluationCount++
    return "evaluated"
})
private fun bottomTailThrows(): String = bottomTail<Nothing> { error("tail") }

class BottomInlineTests {
    @TestAttribute
    fun importedBottomDispatchPreservesNonLocalReturns() {
        check(bottomText(0) == "first")
        check(bottomText(1) == "second")
        check(bottomGeneric(42).value == 42)
        check(bottomGeneric("generic").value == "generic")
        check(bottomGeneric<String?>(null).value == null)
    }

    @TestAttribute
    fun bottomEvaluationAndDefaultExceptionsArePreserved() {
        bottomEvaluationCount = 0
        check(bottomEvaluated() == "evaluated")
        check(bottomEvaluationCount == 2)
        var caught = false
        try { bottomDefaultThrows() } catch (e: IllegalStateException) {
            check(e.message == "unexpected")
            caught = true
        }
        check(caught)
        caught = false
        try { bottomTailThrows() } catch (e: IllegalStateException) {
            check(e.message == "tail")
            caught = true
        }
        check(caught)
    }

    @TestAttribute
    fun nullableBottomStillReturnsItsNullValue() {
        check(nullableBottom { null } == null)
        var evaluations = 0
        val result = bottomTail<Nothing?> { evaluations++; null }
        check(result == null)
        check(evaluations == 1)
        check(bottomDispatch<Nothing?>(0, { null }) == null)
    }
}
