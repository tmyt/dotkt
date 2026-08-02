// Named suspend functions and suspend lambdas must pass through the same try/catch/finally normalization.
// These shapes used to be v1 refusals. Keep both declaration forms and their normal/exceptional routes as execution
// regressions now that handlers and nested protected regions are hoisted into resumable straight-line routes.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

var suspendTryUnsTrace = ""

fun suspendTryUnsOutcome(block: suspend () -> Unit): String =
    try {
        blockOn(block)
        "ok"
    } catch (e: IllegalArgumentException) {
        "iae:${e.message}"
    } catch (e: IllegalStateException) {
        "ise:${e.message}"
    }

suspend fun suspendTryUnsFinallyNamed(failBody: Boolean, replaceInFinally: Boolean) {
    try {
        suspendTryUnsTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        suspendTryUnsTrace += "finally-before;"
        Task.Delay(1).await()
        suspendTryUnsTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

fun suspendTryUnsFinallyLambda(failBody: Boolean, replaceInFinally: Boolean): suspend () -> Unit = {
    try {
        suspendTryUnsTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        suspendTryUnsTrace += "finally-before;"
        Task.Delay(1).await()
        suspendTryUnsTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

suspend fun suspendTryUnsCatchWithFinallyNamed(failAfterCatch: Boolean) {
    try {
        suspendTryUnsTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        suspendTryUnsTrace += "catch-before;"
        Task.Delay(1).await()
        suspendTryUnsTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        suspendTryUnsTrace += "finally;"
    }
}

fun suspendTryUnsCatchWithFinallyLambda(failAfterCatch: Boolean): suspend () -> Unit = {
    try {
        suspendTryUnsTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        suspendTryUnsTrace += "catch-before;"
        Task.Delay(1).await()
        suspendTryUnsTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        suspendTryUnsTrace += "finally;"
    }
}

suspend fun suspendTryUnsNestedTryNamed(failInner: Boolean) {
    try {
        suspendTryUnsTrace += "outer-before;"
        try {
            suspendTryUnsTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            suspendTryUnsTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            suspendTryUnsTrace += "inner-catch;"
            throw e
        } finally {
            suspendTryUnsTrace += "inner-finally;"
        }
        suspendTryUnsTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        suspendTryUnsTrace += "outer-catch;"
    } finally {
        suspendTryUnsTrace += "outer-finally;"
    }
}

fun suspendTryUnsNestedTryLambda(failInner: Boolean): suspend () -> Unit = {
    try {
        suspendTryUnsTrace += "outer-before;"
        try {
            suspendTryUnsTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            suspendTryUnsTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            suspendTryUnsTrace += "inner-catch;"
            throw e
        } finally {
            suspendTryUnsTrace += "inner-finally;"
        }
        suspendTryUnsTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        suspendTryUnsTrace += "outer-catch;"
    } finally {
        suspendTryUnsTrace += "outer-finally;"
    }
}

class SuspendTryLoweringTests {
    @TestAttribute
    fun namedFinallyResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        blockOn { suspendTryUnsFinallyNamed(false, false) }
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun namedCatchWithFinallyResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome { suspendTryUnsCatchWithFinallyNamed(false) })
        assertEquals("try;catch-before;catch-after;finally;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun namedNestedTryResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome { suspendTryUnsNestedTryNamed(false) })
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            suspendTryUnsTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome(suspendTryUnsFinallyLambda(false, false)))
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun lambdaCatchWithFinallyResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome(suspendTryUnsCatchWithFinallyLambda(false)))
        assertEquals("try;catch-before;catch-after;finally;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun lambdaNestedTryResumesInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome(suspendTryUnsNestedTryLambda(false)))
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            suspendTryUnsTrace,
        )
    }

    @TestAttribute
    fun namedFinallyPreservesAndReplacesExceptions() {
        suspendTryUnsTrace = ""
        assertEquals("ise:body", suspendTryUnsOutcome { suspendTryUnsFinallyNamed(true, false) })
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)

        suspendTryUnsTrace = ""
        assertEquals("iae:finally", suspendTryUnsOutcome { suspendTryUnsFinallyNamed(true, true) })
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun namedCatchFailureStillRunsFinallyOnce() {
        suspendTryUnsTrace = ""
        assertEquals("iae:catch", suspendTryUnsOutcome { suspendTryUnsCatchWithFinallyNamed(true) })
        assertEquals("try;catch-before;catch-after;finally;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun namedNestedTryRoutesExceptionInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome { suspendTryUnsNestedTryNamed(true) })
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            suspendTryUnsTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyPreservesAndReplacesExceptions() {
        suspendTryUnsTrace = ""
        assertEquals("ise:body", suspendTryUnsOutcome(suspendTryUnsFinallyLambda(true, false)))
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)

        suspendTryUnsTrace = ""
        assertEquals("iae:finally", suspendTryUnsOutcome(suspendTryUnsFinallyLambda(true, true)))
        assertEquals("body;finally-before;finally-after;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun lambdaCatchFailureStillRunsFinallyOnce() {
        suspendTryUnsTrace = ""
        assertEquals("iae:catch", suspendTryUnsOutcome(suspendTryUnsCatchWithFinallyLambda(true)))
        assertEquals("try;catch-before;catch-after;finally;", suspendTryUnsTrace)
    }

    @TestAttribute
    fun lambdaNestedTryRoutesExceptionInKotlinOrder() {
        suspendTryUnsTrace = ""
        assertEquals("ok", suspendTryUnsOutcome(suspendTryUnsNestedTryLambda(true)))
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            suspendTryUnsTrace,
        )
    }
}
