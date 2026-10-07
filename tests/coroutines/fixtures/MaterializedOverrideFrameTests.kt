package materializedoverrideframe

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.*
import kotlin.coroutines.intrinsics.*

private interface Extended<T> : Continuation<T> { override val context: CoroutineContext }
private class Wrapped<T>(val original: Continuation<T>) : Extended<T> {
    override val context: CoroutineContext get() = original.context
    override fun resumeWith(result: Result<T>) = original.resumeWith(result)
}

private suspend inline fun <T> materializedOverridePause(crossinline block: (Extended<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { original ->
        block(Wrapped(original))
        COROUTINE_SUSPENDED
    }

private suspend fun materializedOverrideValue(value: Long): Long = materializedOverridePause { continuation ->
    check(continuation.context == EmptyCoroutineContext)
    continuation.resume(value)
}

class MaterializedOverrideFrameTests {
    @TestAttribute
    fun calleeOverrideMetadataDoesNotBecomeAClosureTypeParameter() {
        var actual = 0L
        val block: suspend () -> Long = { materializedOverrideValue(42L) }
        block.startCoroutine(object : Continuation<Long> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Long>) { actual = result.getOrThrow() }
        })
        assertEquals(42L, actual)
    }
}
