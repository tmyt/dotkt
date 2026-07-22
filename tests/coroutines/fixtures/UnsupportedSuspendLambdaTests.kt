// #125 — a suspend lambda must pass through the same segmentability classifier as a named suspend function.
// These three v1-unsupported try shapes must produce a valid SuspendLambda SM whose invokeSuspend fails loud with
// NotSupportedException, never an SM containing an unsegmented suspendCall / invalid IL.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

fun corBUnsTouch() {}

fun corBUnsFinallyLambda(): suspend () -> Unit = {
    try {
        corBUnsTouch()
    } finally {
        Task.Delay(1).await()
    }
}

fun corBUnsCatchWithFinallyLambda(): suspend () -> Unit = {
    try {
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        Task.Delay(1).await()
    } finally {
        corBUnsTouch()
    }
}

fun corBUnsNestedTryLambda(): suspend () -> Unit = {
    try {
        try {
            Task.Delay(1).await()
        } catch (e: IllegalStateException) {
            corBUnsTouch()
        }
    } catch (e: Throwable) {
        corBUnsTouch()
    }
}

fun corBUnsFailure(block: suspend () -> Unit): String {
    try {
        blockOn(block)
    } catch (e: UnsupportedOperationException) {
        return e.message ?: ""
    }
    return ""
}

class UnsupportedSuspendLambdaTests {
    @TestAttribute
    fun unsupportedTryShapesFailLoudAtInvocation() {
        assertTrue(corBUnsFailure(corBUnsFinallyLambda()).contains("catch/finally handler"))
        assertTrue(corBUnsFailure(corBUnsCatchWithFinallyLambda()).contains("catch/finally handler"))
        assertTrue(corBUnsFailure(corBUnsNestedTryLambda()).contains("nested inside another suspending try"))
    }
}
