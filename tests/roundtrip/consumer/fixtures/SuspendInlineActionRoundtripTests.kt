import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspendinlineaction.ActionTrace
import roundtrip.suspendinlineaction.runWithAction

private var inlineActionContinuation: Continuation<Int>? = null

private suspend fun pauseInlineAction(trace: ActionTrace): Int = suspendCoroutine {
    trace.text += "P"
    inlineActionContinuation = it
}

private suspend fun returnFromInlineAction(trace: ActionTrace): Int {
    runWithAction(trace, "owner") {
        val value = pauseInlineAction(trace)
        trace.text += "R"
        return value
    }
    return -1
}

class SuspendInlineActionRoundtripTests {
    @TestAttribute
    fun suspendInlineActionKeepsDefaultsExplicitOwnerAndFinally() {
        for (explicitOwner in listOf(false, true)) {
            inlineActionContinuation = null
            val trace = ActionTrace()
            var completed = false
            var actual = 0
            val operation: suspend () -> Int = {
                if (explicitOwner) {
                    runWithAction(trace, "owner") {
                        val value = pauseInlineAction(trace)
                        trace.text += "R"
                        value + 1
                    }
                } else {
                    runWithAction(trace) {
                        val value = pauseInlineAction(trace)
                        trace.text += "R"
                        value + 1
                    }
                }
            }
            operation.startCoroutine(object : Continuation<Int> {
                override val context: CoroutineContext = EmptyCoroutineContext
                override fun resumeWith(result: Result<Int>) {
                    actual = result.getOrThrow()
                    completed = true
                }
            })
            check(!completed && trace.text == if (explicitOwner) "OP" else "EP")
            val continuation = inlineActionContinuation ?: error("did not suspend")
            inlineActionContinuation = null
            continuation.resume(41)
            check(completed && actual == 42)
            check(trace.text == if (explicitOwner) "OPRX" else "EPRF")
        }
    }

    @TestAttribute
    fun suspendInlineActionRunsFinallyAfterResumedFailure() {
        inlineActionContinuation = null
        val trace = ActionTrace()
        var completed = false
        var failure: Throwable? = null
        val operation: suspend () -> Int = {
            runWithAction<Int>(trace) {
                pauseInlineAction(trace)
                trace.text += "R"
                error("inline action failure")
            }
        }
        operation.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                failure = result.exceptionOrNull()
                completed = true
            }
        })
        check(!completed && trace.text == "EP")
        val continuation = inlineActionContinuation ?: error("did not suspend")
        inlineActionContinuation = null
        continuation.resume(0)
        check(completed && failure?.message == "inline action failure")
        check(trace.text == "EPRF")
    }

    @TestAttribute
    fun suspendInlineActionPreservesNonLocalReturnAcrossFinally() {
        inlineActionContinuation = null
        val trace = ActionTrace()
        var completed = false
        var actual = 0
        val operation: suspend () -> Int = { returnFromInlineAction(trace) }
        operation.startCoroutine(object : Continuation<Int> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {
                actual = result.getOrThrow()
                completed = true
            }
        })
        check(!completed && trace.text == "OP")
        val continuation = inlineActionContinuation ?: error("did not suspend")
        inlineActionContinuation = null
        continuation.resume(43)
        check(completed && actual == 43)
        check(trace.text == "OPRX")
    }
}
