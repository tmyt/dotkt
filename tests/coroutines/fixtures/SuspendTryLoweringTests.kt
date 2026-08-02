// Named suspend functions and suspend lambdas must pass through the same try/catch/finally normalization.
// These shapes used to be v1 refusals. Keep both declaration forms and their normal/exceptional routes as execution
// regressions now that handlers and nested protected regions are hoisted into resumable straight-line routes.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

var suspendTryTrace = ""

fun suspendTryOutcome(block: suspend () -> Unit): String =
    try {
        blockOn(block)
        "ok"
    } catch (e: IllegalArgumentException) {
        "iae:${e.message}"
    } catch (e: IllegalStateException) {
        "ise:${e.message}"
    }

suspend fun suspendTryFinallyNamed(failBody: Boolean, replaceInFinally: Boolean) {
    try {
        suspendTryTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        suspendTryTrace += "finally-before;"
        Task.Delay(1).await()
        suspendTryTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

fun suspendTryFinallyLambda(failBody: Boolean, replaceInFinally: Boolean): suspend () -> Unit = {
    try {
        suspendTryTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        suspendTryTrace += "finally-before;"
        Task.Delay(1).await()
        suspendTryTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

suspend fun suspendTryCatchWithFinallyNamed(failAfterCatch: Boolean) {
    try {
        suspendTryTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        suspendTryTrace += "catch-before;"
        Task.Delay(1).await()
        suspendTryTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        suspendTryTrace += "finally;"
    }
}

fun suspendTryCatchWithFinallyLambda(failAfterCatch: Boolean): suspend () -> Unit = {
    try {
        suspendTryTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        suspendTryTrace += "catch-before;"
        Task.Delay(1).await()
        suspendTryTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        suspendTryTrace += "finally;"
    }
}

suspend fun suspendTryNestedTryNamed(failInner: Boolean) {
    try {
        suspendTryTrace += "outer-before;"
        try {
            suspendTryTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            suspendTryTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            suspendTryTrace += "inner-catch;"
            throw e
        } finally {
            suspendTryTrace += "inner-finally;"
        }
        suspendTryTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        suspendTryTrace += "outer-catch;"
    } finally {
        suspendTryTrace += "outer-finally;"
    }
}

fun suspendTryNestedTryLambda(failInner: Boolean): suspend () -> Unit = {
    try {
        suspendTryTrace += "outer-before;"
        try {
            suspendTryTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            suspendTryTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            suspendTryTrace += "inner-catch;"
            throw e
        } finally {
            suspendTryTrace += "inner-finally;"
        }
        suspendTryTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        suspendTryTrace += "outer-catch;"
    } finally {
        suspendTryTrace += "outer-finally;"
    }
}

class SuspendTryLoweringTests {
    @TestAttribute
    fun namedFinallyResumesInKotlinOrder() {
        suspendTryTrace = ""
        blockOn { suspendTryFinallyNamed(false, false) }
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)
    }

    @TestAttribute
    fun namedCatchWithFinallyResumesInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome { suspendTryCatchWithFinallyNamed(false) })
        assertEquals("try;catch-before;catch-after;finally;", suspendTryTrace)
    }

    @TestAttribute
    fun namedNestedTryResumesInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome { suspendTryNestedTryNamed(false) })
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            suspendTryTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyResumesInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome(suspendTryFinallyLambda(false, false)))
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)
    }

    @TestAttribute
    fun lambdaCatchWithFinallyResumesInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome(suspendTryCatchWithFinallyLambda(false)))
        assertEquals("try;catch-before;catch-after;finally;", suspendTryTrace)
    }

    @TestAttribute
    fun lambdaNestedTryResumesInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome(suspendTryNestedTryLambda(false)))
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            suspendTryTrace,
        )
    }

    @TestAttribute
    fun namedFinallyPreservesAndReplacesExceptions() {
        suspendTryTrace = ""
        assertEquals("ise:body", suspendTryOutcome { suspendTryFinallyNamed(true, false) })
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)

        suspendTryTrace = ""
        assertEquals("iae:finally", suspendTryOutcome { suspendTryFinallyNamed(true, true) })
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)
    }

    @TestAttribute
    fun namedCatchFailureStillRunsFinallyOnce() {
        suspendTryTrace = ""
        assertEquals("iae:catch", suspendTryOutcome { suspendTryCatchWithFinallyNamed(true) })
        assertEquals("try;catch-before;catch-after;finally;", suspendTryTrace)
    }

    @TestAttribute
    fun namedNestedTryRoutesExceptionInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome { suspendTryNestedTryNamed(true) })
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            suspendTryTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyPreservesAndReplacesExceptions() {
        suspendTryTrace = ""
        assertEquals("ise:body", suspendTryOutcome(suspendTryFinallyLambda(true, false)))
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)

        suspendTryTrace = ""
        assertEquals("iae:finally", suspendTryOutcome(suspendTryFinallyLambda(true, true)))
        assertEquals("body;finally-before;finally-after;", suspendTryTrace)
    }

    @TestAttribute
    fun lambdaCatchFailureStillRunsFinallyOnce() {
        suspendTryTrace = ""
        assertEquals("iae:catch", suspendTryOutcome(suspendTryCatchWithFinallyLambda(true)))
        assertEquals("try;catch-before;catch-after;finally;", suspendTryTrace)
    }

    @TestAttribute
    fun lambdaNestedTryRoutesExceptionInKotlinOrder() {
        suspendTryTrace = ""
        assertEquals("ok", suspendTryOutcome(suspendTryNestedTryLambda(true)))
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            suspendTryTrace,
        )
    }
}
