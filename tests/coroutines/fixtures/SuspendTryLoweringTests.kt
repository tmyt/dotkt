// Named suspend functions and suspend lambdas must pass through the same try/catch/finally normalization.
// These shapes used to be v1 refusals. Keep both declaration forms and their normal/exceptional routes as execution
// regressions now that handlers and nested protected regions are hoisted into resumable straight-line routes.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

var corBUnsTrace = ""

fun corBUnsOutcome(block: suspend () -> Unit): String =
    try {
        blockOn(block)
        "ok"
    } catch (e: IllegalArgumentException) {
        "iae:${e.message}"
    } catch (e: IllegalStateException) {
        "ise:${e.message}"
    }

suspend fun corBUnsFinallyNamed(failBody: Boolean, replaceInFinally: Boolean) {
    try {
        corBUnsTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        corBUnsTrace += "finally-before;"
        Task.Delay(1).await()
        corBUnsTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

fun corBUnsFinallyLambda(failBody: Boolean, replaceInFinally: Boolean): suspend () -> Unit = {
    try {
        corBUnsTrace += "body;"
        if (failBody) throw IllegalStateException("body")
    } finally {
        corBUnsTrace += "finally-before;"
        Task.Delay(1).await()
        corBUnsTrace += "finally-after;"
        if (replaceInFinally) throw IllegalArgumentException("finally")
    }
}

suspend fun corBUnsCatchWithFinallyNamed(failAfterCatch: Boolean) {
    try {
        corBUnsTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        corBUnsTrace += "catch-before;"
        Task.Delay(1).await()
        corBUnsTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        corBUnsTrace += "finally;"
    }
}

fun corBUnsCatchWithFinallyLambda(failAfterCatch: Boolean): suspend () -> Unit = {
    try {
        corBUnsTrace += "try;"
        throw IllegalStateException("boom")
    } catch (e: IllegalStateException) {
        corBUnsTrace += "catch-before;"
        Task.Delay(1).await()
        corBUnsTrace += "catch-after;"
        if (failAfterCatch) throw IllegalArgumentException("catch")
    } finally {
        corBUnsTrace += "finally;"
    }
}

suspend fun corBUnsNestedTryNamed(failInner: Boolean) {
    try {
        corBUnsTrace += "outer-before;"
        try {
            corBUnsTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            corBUnsTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            corBUnsTrace += "inner-catch;"
            throw e
        } finally {
            corBUnsTrace += "inner-finally;"
        }
        corBUnsTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        corBUnsTrace += "outer-catch;"
    } finally {
        corBUnsTrace += "outer-finally;"
    }
}

fun corBUnsNestedTryLambda(failInner: Boolean): suspend () -> Unit = {
    try {
        corBUnsTrace += "outer-before;"
        try {
            corBUnsTrace += "inner-before;"
            Task.Delay(1).await()
            if (failInner) throw IllegalStateException("inner")
            corBUnsTrace += "inner-after;"
        } catch (e: IllegalStateException) {
            corBUnsTrace += "inner-catch;"
            throw e
        } finally {
            corBUnsTrace += "inner-finally;"
        }
        corBUnsTrace += "outer-after;"
    } catch (e: IllegalStateException) {
        corBUnsTrace += "outer-catch;"
    } finally {
        corBUnsTrace += "outer-finally;"
    }
}

class SuspendTryLoweringTests {
    @TestAttribute
    fun namedFinallyResumesInKotlinOrder() {
        corBUnsTrace = ""
        blockOn { corBUnsFinallyNamed(false, false) }
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)
    }

    @TestAttribute
    fun namedCatchWithFinallyResumesInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome { corBUnsCatchWithFinallyNamed(false) })
        assertEquals("try;catch-before;catch-after;finally;", corBUnsTrace)
    }

    @TestAttribute
    fun namedNestedTryResumesInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome { corBUnsNestedTryNamed(false) })
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            corBUnsTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyResumesInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome(corBUnsFinallyLambda(false, false)))
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)
    }

    @TestAttribute
    fun lambdaCatchWithFinallyResumesInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome(corBUnsCatchWithFinallyLambda(false)))
        assertEquals("try;catch-before;catch-after;finally;", corBUnsTrace)
    }

    @TestAttribute
    fun lambdaNestedTryResumesInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome(corBUnsNestedTryLambda(false)))
        assertEquals(
            "outer-before;inner-before;inner-after;inner-finally;outer-after;outer-finally;",
            corBUnsTrace,
        )
    }

    @TestAttribute
    fun namedFinallyPreservesAndReplacesExceptions() {
        corBUnsTrace = ""
        assertEquals("ise:body", corBUnsOutcome { corBUnsFinallyNamed(true, false) })
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)

        corBUnsTrace = ""
        assertEquals("iae:finally", corBUnsOutcome { corBUnsFinallyNamed(true, true) })
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)
    }

    @TestAttribute
    fun namedCatchFailureStillRunsFinallyOnce() {
        corBUnsTrace = ""
        assertEquals("iae:catch", corBUnsOutcome { corBUnsCatchWithFinallyNamed(true) })
        assertEquals("try;catch-before;catch-after;finally;", corBUnsTrace)
    }

    @TestAttribute
    fun namedNestedTryRoutesExceptionInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome { corBUnsNestedTryNamed(true) })
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            corBUnsTrace,
        )
    }

    @TestAttribute
    fun lambdaFinallyPreservesAndReplacesExceptions() {
        corBUnsTrace = ""
        assertEquals("ise:body", corBUnsOutcome(corBUnsFinallyLambda(true, false)))
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)

        corBUnsTrace = ""
        assertEquals("iae:finally", corBUnsOutcome(corBUnsFinallyLambda(true, true)))
        assertEquals("body;finally-before;finally-after;", corBUnsTrace)
    }

    @TestAttribute
    fun lambdaCatchFailureStillRunsFinallyOnce() {
        corBUnsTrace = ""
        assertEquals("iae:catch", corBUnsOutcome(corBUnsCatchWithFinallyLambda(true)))
        assertEquals("try;catch-before;catch-after;finally;", corBUnsTrace)
    }

    @TestAttribute
    fun lambdaNestedTryRoutesExceptionInKotlinOrder() {
        corBUnsTrace = ""
        assertEquals("ok", corBUnsOutcome(corBUnsNestedTryLambda(true)))
        assertEquals(
            "outer-before;inner-before;inner-catch;inner-finally;outer-catch;outer-finally;",
            corBUnsTrace,
        )
    }
}
