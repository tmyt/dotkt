import ExternalAwaitable.MemberOperation
import ExternalAwaitable.Operation
import ExternalAwaitable.await
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Threading.ManualResetEventSlim
import dotkt.support.blockOn
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private fun <T> completeExternalAwait(complete: () -> Unit, block: suspend () -> T): T {
    val done = ManualResetEventSlim(false)
    var outcome: Result<T>? = null
    block.startCoroutine(object : Continuation<T> {
        override val context get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<T>) {
            outcome = result
            done.Set()
        }
    })
    // The Task is incomplete until startCoroutine has returned, so this must use the cold path.
    assertTrue(!done.IsSet)
    complete()
    assertTrue(done.Wait(10000))
    return outcome!!.getOrThrow()
}

class ExternalAwaitTests {
    @TestAttribute
    fun extensionReturningFrameworkAwaiterPreservesGenericResultsAndSuspends() {
        assertEquals(8, blockOn { Operation<Int>(7, true).await() + 1 })
        assertEquals("ready!", blockOn { Operation<String>("ready", true).await() + "!" })
        val number = Operation<Int>(0, false)
        assertEquals(42, completeExternalAwait({ number.Complete(41) }) { number.await() + 1 })
        val text = Operation<String>("unused", false)
        assertEquals("resumed!", completeExternalAwait({ text.Complete("resumed") }) { text.await() + "!" })
    }

    @TestAttribute
    fun memberReturningFrameworkAwaiterPreservesGenericResultsAndSuspends() {
        assertEquals(8, blockOn { MemberOperation<Int>(7, true).await() + 1 })
        assertEquals("ready!", blockOn { MemberOperation<String>("ready", true).await() + "!" })
        val number = MemberOperation<Int>(0, false)
        assertEquals(42, completeExternalAwait({ number.Complete(41) }) { number.await() + 1 })
        val text = MemberOperation<String>("unused", false)
        assertEquals("resumed!", completeExternalAwait({ text.Complete("resumed") }) { text.await() + "!" })
    }
}
