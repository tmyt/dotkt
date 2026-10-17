import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task1
import System.Type
import dotkt.support.blockOn
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.resume
import kotlin.coroutines.startCoroutine
import kotlin.coroutines.suspendCoroutine

class CoroutineGuardValue(val matchesEverything: Boolean = false) {
    var calls = 0
    override fun equals(other: Any?): Boolean {
        calls++
        return matchesEverything || other is CoroutineGuardValue
    }
    override fun hashCode(): Int = 1
}

class CoroutineGuardFailure : Exception("expected") {
    var calls = 0
    override fun equals(other: Any?): Boolean { calls++; return true }
    override fun hashCode(): Int = 1
}

private suspend fun compilerGuardPass(value: CoroutineGuardValue): CoroutineGuardValue = value
private suspend fun compilerGuardSafe(value: CoroutineGuardValue): CoroutineGuardValue =
    suspendCoroutine { it.resume(value) }
private suspend fun compilerGuardUnintercepted(value: CoroutineGuardValue): CoroutineGuardValue =
    suspendCoroutineUninterceptedOrReturn { value }

class CoroutineGuardTaskProvider(val result: CoroutineGuardValue) {
    suspend fun provide(): CoroutineGuardValue = result
}

class CoroutineGuardSuspendingTaskProvider {
    var pending: Continuation<String>? = null
    suspend fun provide(): String = suspendCoroutine { pending = it }
}

class CompilerGuardEqualityTests {
    @TestAttribute
    fun synchronousColdCallsAndStoredSuspendLambdasDoNotCallEquals() {
        val value = CoroutineGuardValue()
        assertEquals(true, blockOn { compilerGuardPass(value) } === value)
        assertEquals(0, value.calls)
        val body: suspend () -> CoroutineGuardValue = { compilerGuardPass(value) }
        assertEquals(true, blockOn(body) === value)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun synchronousCoroutineIntrinsicsDoNotCallEquals() {
        val value = CoroutineGuardValue()
        assertEquals(true, blockOn { compilerGuardSafe(value) } === value)
        assertEquals(0, value.calls)
        assertEquals(true, blockOn { compilerGuardUnintercepted(value) } === value)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun resumedColdCallsPreserveValueIdentityWithoutEquals() {
        val value = CoroutineGuardValue()
        var pending: Continuation<CoroutineGuardValue>? = null
        var observed: CoroutineGuardValue? = null
        var completed = false
        var failure: Throwable? = null
        val body: suspend () -> Unit = {
            val resumed = suspendCoroutine<CoroutineGuardValue> { pending = it }
            observed = compilerGuardPass(resumed)
        }
        body.startCoroutine(object : Continuation<Unit> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) {
                failure = result.exceptionOrNull()
                completed = true
            }
        })
        assertEquals(false, completed)
        pending!!.resume(value)
        assertEquals(true, completed)
        assertEquals(true, failure === null)
        assertEquals(true, observed === value)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun taskBridgeDoesNotTreatAnEqualValueAsTheSuspensionSentinel() {
        val value = CoroutineGuardValue(matchesEverything = true)
        val provider = CoroutineGuardTaskProvider(value)
        val task = Type.GetType("CoroutineGuardTaskProvider")!!.GetMethod("provide")!!
            .Invoke(provider, null) as Task1<CoroutineGuardValue>
        // Check completion before awaiting, so a sentinel mistake fails instead of hanging this test.
        assertEquals(true, task.IsCompleted)
        assertEquals(true, task.GetAwaiter().GetResult() === value)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun asynchronouslyFailedTaskPreservesExceptionIdentityWithoutEquals() {
        val cause = CoroutineGuardFailure()
        val provider = CoroutineGuardSuspendingTaskProvider()
        val task = Type.GetType("CoroutineGuardSuspendingTaskProvider")!!.GetMethod("provide")!!
            .Invoke(provider, null) as Task1<String>
        assertEquals(false, task.IsCompleted)
        provider.pending!!.resumeWith(Result.failure(cause))
        assertEquals(true, task.IsFaulted)
        var observed: Throwable? = null
        try {
            task.GetAwaiter().GetResult()
        } catch (failure: Throwable) {
            observed = failure
        }
        assertEquals(true, observed === cause)
        assertEquals(0, cause.calls)
    }

    @TestAttribute
    fun suspendingCatchHandlersDoNotCompareExceptionsWithEquals() {
        val cause = CoroutineGuardFailure()
        val value = CoroutineGuardValue()
        var caught = false
        blockOn {
            try {
                throw cause
            } catch (failure: CoroutineGuardFailure) {
                assertEquals(true, failure === cause)
                compilerGuardPass(value)
                caught = true
            }
        }
        assertEquals(true, caught)
        assertEquals(0, cause.calls)
        assertEquals(0, value.calls)
    }

    @TestAttribute
    fun suspendingFinallyPreservesPendingExceptionsWithoutEquals() {
        val cause = CoroutineGuardFailure()
        val value = CoroutineGuardValue()
        var observed: Throwable? = null
        var finalized = false
        try {
            blockOn {
                try {
                    throw cause
                } finally {
                    compilerGuardPass(value)
                    finalized = true
                }
            }
        } catch (failure: Throwable) {
            observed = failure
        }
        assertEquals(true, finalized)
        assertEquals(true, observed === cause)
        assertEquals(0, cause.calls)
        assertEquals(0, value.calls)
    }
}
