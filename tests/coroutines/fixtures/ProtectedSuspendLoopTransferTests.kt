import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Threading.ManualResetEventSlim
import System.Threading.Tasks.Task
import System.Threading.Tasks.TaskCompletionSource1
import dotkt.support.blockOn
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private suspend fun protectedSuspendGatedTrace(continueFirst: Boolean, gate: TaskCompletionSource1<Int>): String {
    var trace = ""
    var index = 0
    while (index < 2) {
        index++
        try {
            if (continueFirst && index == 1) continue
            break
        } finally {
            gate.Task.await()
            trace += "f"
        }
    }
    return trace
}

suspend fun protectedSuspendLoopTrace(): String {
    var trace = ""
    for (index in 0..3) {
        try {
            if (index == 0) continue
            if (index == 2) break
            trace += "b"
        } finally {
            Task.Delay(1).await()
            trace += "f"
        }
    }
    return trace
}

suspend fun protectedSuspendNestedCatchTrace(): String {
    var trace = ""
    outer@ for (index in 0..2) {
        try {
            for (inner in intArrayOf(0, 1)) {
                try {
                    if (inner == 0) continue
                    throw IllegalStateException("caught")
                } catch (failure: IllegalStateException) {
                    if (index == 0) continue@outer
                    break@outer
                } finally {
                    Task.Delay(1).await()
                    trace += "i"
                }
            }
        } finally {
            Task.Delay(1).await()
            trace += "o"
        }
    }
    return trace
}

suspend fun protectedSuspendOverrideTrace(): String {
    var trace = ""
    for (index in 0..3) {
        try {
            trace += "t"
            if (index == 0) break
            throw IllegalStateException("suppressed")
        } finally {
            Task.Delay(1).await()
            trace += "f"
            if (index < 2) continue
            break
        }
    }
    return trace
}

class ProtectedSuspendLoopTransferTests {
    @TestAttribute
    fun continueAndBreakCleanupReallySuspendBeforeResuming() {
        for (continueFirst in listOf(false, true)) {
            val gate = TaskCompletionSource1<Int>()
            val done = ManualResetEventSlim(false)
            var value: String? = null
            var failure: Throwable? = null
            val work: suspend () -> String = { protectedSuspendGatedTrace(continueFirst, gate) }
            work.startCoroutine(object : Continuation<String> {
                override val context get() = EmptyCoroutineContext
                override fun resumeWith(result: Result<String>) {
                    value = result.getOrNull()
                    failure = result.exceptionOrNull()
                    done.Set()
                }
            })
            // The only Task is deliberately incomplete until AFTER startCoroutine returns: a fast path cannot
            // satisfy this assertion. Resuming must retain the pending continue/break and execute cleanup once.
            assertTrue(!done.IsSet)
            gate.SetResult(1)
            assertTrue(done.Wait(10000))
            failure?.let { throw it }
            assertEquals(if (continueFirst) "ff" else "f", value)
        }
    }

    @TestAttribute
    fun cleanupRunsOnceOnEveryLoopExitAcrossSuspension() {
        assertEquals("fbff", blockOn { protectedSuspendLoopTrace() })
    }

    @TestAttribute
    fun nestedCatchExitsRunBothSuspendingCleanups() {
        assertEquals("iioiio", blockOn { protectedSuspendNestedCatchTrace() })
    }

    @TestAttribute
    fun suspendingCleanupMayReplaceBreakAndException() {
        assertEquals("tftftf", blockOn { protectedSuspendOverrideTrace() })
    }
}
