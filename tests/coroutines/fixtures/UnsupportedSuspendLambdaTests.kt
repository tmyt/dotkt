// A suspend lambda must pass through the same try/catch/finally normalization as a named suspend function.
// These shapes used to be v1 refusals. Keep them as execution regressions now that handlers and nested protected
// regions are hoisted into resumable straight-line routes.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

var corBUnsTrace = ""

fun corBUnsFinallyLambda(): suspend () -> Unit = {
    try {
        corBUnsTrace += "body;"
    } finally {
        corBUnsTrace += "finally-before;"
        Task.Delay(1).await()
        corBUnsTrace += "finally-after;"
    }
}

fun corBUnsCatchWithFinallyLambda(): suspend () -> Unit = {
    try {
        corBUnsTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        corBUnsTrace += "catch-before;"
        Task.Delay(1).await()
        corBUnsTrace += "catch-after;"
    } finally {
        corBUnsTrace += "finally;"
    }
}

fun corBUnsNestedTryLambda(): suspend () -> Unit = {
    try {
        corBUnsTrace += "outer-before;"
        try {
            corBUnsTrace += "inner-before;"
            Task.Delay(1).await()
            corBUnsTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            corBUnsTrace += "inner-catch;"
        }
        corBUnsTrace += "outer-after;"
    } catch (e: Throwable) {
        corBUnsTrace += "outer-catch;"
    }
}

class SuspendTryLambdaTests {
    @TestAttribute
    fun tryShapesResumeInKotlinOrder() {
        corBUnsTrace = ""
        blockOn(corBUnsFinallyLambda())
        assertTrue(corBUnsTrace == "body;finally-before;finally-after;")

        corBUnsTrace = ""
        blockOn(corBUnsCatchWithFinallyLambda())
        assertTrue(corBUnsTrace == "try;catch-before;catch-after;finally;")

        corBUnsTrace = ""
        blockOn(corBUnsNestedTryLambda())
        assertTrue(corBUnsTrace == "outer-before;inner-before;inner-after;outer-after;")
    }
}
