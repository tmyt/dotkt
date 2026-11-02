import ExternalAwaitable.MemberOperation
import ExternalAwaitable.Operation
import ExternalAwaitable.OptionalOperation
import ExternalAwaitable.OptionalMemberOperation
import ExternalAwaitable.TextOperation
import ExternalAwaitable.NullableTextOperation
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
    fun externalGenericNullableResultsRemainNullableOnBothPaths() {
        val immediate: String? = blockOn { OptionalOperation<String>(true).await() }
        assertTrue(immediate == null)
        val pending = OptionalOperation<String>(false)
        val resumed: String? = completeExternalAwait({ pending.Complete() }) { pending.await() }
        assertTrue(resumed == null)
        val memberImmediate: String? = blockOn { OptionalMemberOperation<String>(true).await() }
        assertTrue(memberImmediate == null)
        val memberPending = OptionalMemberOperation<String>(false)
        val memberResumed: String? = completeExternalAwait({ memberPending.Complete() }) { memberPending.await() }
        assertTrue(memberResumed == null)
    }

    @TestAttribute
    fun externalConcreteResultsUseTheAwaiterAssemblyNullability() {
        val immediate: String = blockOn { TextOperation("ready", true).await() }
        assertEquals("ready", immediate)
        val pending = TextOperation("unused", false)
        val resumed: String = completeExternalAwait({ pending.Complete("resumed") }) { pending.await() }
        assertEquals("resumed", resumed)
        val missing: String? = blockOn { NullableTextOperation(null, true).await() }
        assertTrue(missing == null)
        val nullablePending = NullableTextOperation("unused", false)
        val nullableResumed: String? = completeExternalAwait({ nullablePending.Complete(null) }) { nullablePending.await() }
        assertTrue(nullableResumed == null)
    }

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
