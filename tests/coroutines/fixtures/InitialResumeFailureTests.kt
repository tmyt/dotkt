import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.createCoroutine
import kotlin.coroutines.intrinsics.createCoroutineUnintercepted
import kotlin.coroutines.suspendCoroutine

class InitialResumeCompletion : Continuation<Unit> {
    override val context = EmptyCoroutineContext
    var calls = 0
    var failure: Throwable? = null

    override fun resumeWith(result: Result<Unit>) {
        calls += 1
        failure = result.exceptionOrNull()
    }
}

class InitialResumeFailureTests {
    @TestAttribute
    fun initialFailurePrecedesSequenceBody() {
        var entered = false
        val completion = InitialResumeCompletion()
        val cause = IllegalStateException("failure before sequence body")
        val body: suspend () -> Unit = {
            for (value in sequenceOf(1)) entered = true
        }

        body.createCoroutineUnintercepted(completion).resumeWith(Result.failure(cause))

        assertEquals(false, entered)
        assertEquals(1, completion.calls)
        assertEquals(true, completion.failure === cause)
    }

    @TestAttribute
    fun failureBeforeNonSuspendingBodyPreservesException() {
        var entered = false
        val completion = InitialResumeCompletion()
        val cause = IllegalStateException("initial failure")
        val body: suspend () -> Unit = { entered = true }

        body.createCoroutineUnintercepted(completion).resumeWith(Result.failure(cause))

        assertEquals(false, entered)
        assertEquals(1, completion.calls)
        assertEquals(true, completion.failure === cause)
    }

    @TestAttribute
    fun failureBeforeSuspendingBodyDoesNotEnterHandlers() {
        var entered = false
        var caught = false
        var finalized = false
        val completion = InitialResumeCompletion()
        val cause = IllegalStateException("initial failure")
        val body: suspend () -> Unit = {
            try {
                entered = true
                suspendCoroutine<Unit> { error("body must not start") }
            } catch (_: IllegalStateException) {
                caught = true
            } finally {
                finalized = true
            }
        }

        body.createCoroutineUnintercepted(completion).resumeWith(Result.failure(cause))

        assertEquals(false, entered)
        assertEquals(false, caught)
        assertEquals(false, finalized)
        assertEquals(1, completion.calls)
        assertEquals(true, completion.failure === cause)
    }

    @TestAttribute
    fun receiverFailureAndSuccessfulFirstResumeRemainDistinct() {
        var observed = ""
        val cause = IllegalStateException("receiver initial failure")
        val body: suspend String.() -> Unit = { observed = this }
        val failed = InitialResumeCompletion()

        body.createCoroutineUnintercepted("rejected", failed).resumeWith(Result.failure(cause))

        assertEquals("", observed)
        assertEquals(1, failed.calls)
        assertEquals(true, failed.failure === cause)

        val succeeded = InitialResumeCompletion()
        body.createCoroutineUnintercepted("accepted", succeeded).resumeWith(Result.success(Unit))

        assertEquals("accepted", observed)
        assertEquals(1, succeeded.calls)
        assertEquals(null, succeeded.failure)
    }

    @TestAttribute
    fun interceptedCreationPreservesInitialFailure() {
        var entered = false
        val completion = InitialResumeCompletion()
        val cause = IllegalStateException("intercepted initial failure")
        val body: suspend () -> Unit = { entered = true }

        body.createCoroutine(completion).resumeWith(Result.failure(cause))

        assertEquals(false, entered)
        assertEquals(1, completion.calls)
        assertEquals(true, completion.failure === cause)
    }

    @TestAttribute
    fun successfulStartThenFailedResumeUsesUserHandlers() {
        var entered = false
        var caught: Throwable? = null
        var finalizations = 0
        var pending: Continuation<Unit>? = null
        val completion = InitialResumeCompletion()
        val cause = IllegalStateException("failure after suspension")
        val body: suspend () -> Unit = {
            entered = true
            try {
                suspendCoroutine<Unit> { pending = it }
            } catch (failure: IllegalStateException) {
                caught = failure
            } finally {
                finalizations += 1
            }
        }

        body.createCoroutineUnintercepted(completion).resumeWith(Result.success(Unit))
        assertEquals(true, entered)
        assertEquals(0, completion.calls)
        assertEquals(0, finalizations)

        pending!!.resumeWith(Result.failure(cause))

        assertEquals(true, caught === cause)
        assertEquals(1, finalizations)
        assertEquals(1, completion.calls)
        assertEquals(null, completion.failure)
    }
}
