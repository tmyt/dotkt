import NUnit.Framework.TestAttribute
import nonreturninginline.*
import kotlin.coroutines.*

private inline fun <R> localSpin(step: () -> Unit): R { while (true) step() }
private fun <T> nonReturningValue(value: T): T = spinWhile<T> { return value }
private fun doWhileValue(value: Int): Int = spinDoWhile<Int> { return value }
private fun localSpinValue(): String = localSpin<String> { return "local" }
private var nonReturningFinallyCount = 0
private fun loopWithFinally(): String {
    try { return spinWhile<String> { return "finally" } }
    finally { nonReturningFinallyCount++ }
}
private fun nestedDeclarationValue(value: String): String = spinWhile<String> {
    class Local { fun read(): String = value }
    return Local().read()
}
private fun concreteSpinValue(): Int = concreteSpin { return 47 }
private fun nestedSpinValue(): String = nestedSpin<String> { return "forwarded" }
private fun statementSpinValue(): String {
    spinWhile<String> { return "statement" }
    error("unreachable")
}
private fun calleeFinallyValue(): String = spinWithFinally<String>(
    { return "callee finally" }, { nonReturningFinallyCount++ })
private suspend fun <T> suspendedSpinValue(value: T): T = suspendingSpin<T>(
    { suspendCoroutine<Unit> { pendingNonReturning = it } }, { return value })

class NonReturningInlineTests {
    @TestAttribute
    fun nonReturningLoopsPreserveConcreteAndGenericResults() {
        check(nonReturningValue("reference") == "reference")
        check(nonReturningValue(31) == 31)
        check(nonReturningValue<Int?>(null) == null)
        check(nonReturningValue<Int?>(17) == 17)
        check(doWhileValue(23) == 23)
        check(localSpinValue() == "local")
    }

    @TestAttribute
    fun nonReturningBodiesPreserveEffectsAndExceptions() {
        var effects = 0
        var caught = false
        try { throwAfterStep<String> { effects++ } }
        catch (e: IllegalStateException) { check(e.message == "after step"); caught = true }
        check(caught && effects == 1)
        nonReturningFinallyCount = 0
        check(loopWithFinally() == "finally")
        check(nonReturningFinallyCount == 1)
        check(nestedDeclarationValue("nested") == "nested")
    }

    @TestAttribute
    fun ordinaryUnitAndReturningBodiesStillComplete() {
        var effects = 0
        runUnitStep { effects++ }
        check(effects == 1)
        check(returnStep { effects++; "tail" } == "tail")
        check(effects == 2)
        check(returnStep<Int?> { null } == null)
    }

    @TestAttribute
    fun calleeContinuationsPreserveFinallyAndSuspension() {
        check(concreteSpinValue() == 47)
        check(nestedSpinValue() == "forwarded")
        check(statementSpinValue() == "statement")
        nonReturningFinallyCount = 0
        check(calleeFinallyValue() == "callee finally")
        check(nonReturningFinallyCount == 1)
        var completed = false
        var answer = 0
        val work: suspend () -> Int = { suspendedSpinValue(53) }
        work.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                answer = result.getOrThrow()
                completed = true
            }
        })
        check(!completed)
        val continuation = pendingNonReturning ?: error("did not suspend")
        pendingNonReturning = null
        continuation.resume(Unit)
        check(completed && answer == 53)
    }
}
