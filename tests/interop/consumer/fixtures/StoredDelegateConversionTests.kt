import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.ThreadStart
import System.Threading.SendOrPostCallback
import Delegobj.ArityCb1
import Cbk.Transform
import Cbk.GenericResult
import CbkUnit.NullaryResult
import StoredCallbacks.Legacy
import kotlin.coroutines.*

private fun storedThreadStart(callback: () -> Unit): ThreadStart = ThreadStart(callback)
private fun <F : () -> Unit> boundedThreadStart(callback: F): ThreadStart = ThreadStart(callback)
private val storedPropertyValue: Int get() = 27
private fun <T> storedGenericCallback(callback: (T) -> Unit): ArityCb1<T> = ArityCb1<T>(callback)
private fun <T> storedGenericResult(callback: (T) -> Any?): GenericResult<T> = GenericResult<T>(callback)
private inline fun inlineStoredCallback(noinline callback: () -> Unit) = ThreadStart(callback)
private fun consumeStoredCallback(prefix: Int, callback: ThreadStart) { check(prefix == 1); callback() }

private class SuspendedCallbackSource {
    var reads = 0
    var pending: Continuation<() -> Unit>? = null
    suspend fun next(): () -> Unit = suspendCoroutine { continuation ->
        reads++
        pending = continuation
    }
}

private class StoredCallbackSource(val callback: () -> Unit) {
    var reads = 0
    fun next(): () -> Unit {
        reads++
        return callback
    }
}

class StoredDelegateConversionTests {
    @TestAttribute
    fun inlineAndTryOperandsKeepTheirConversion() {
        var calls = 0
        val callback: () -> Unit = { calls++ }
        ThreadStart(run { callback })()
        inlineStoredCallback(callback)()
        var order = ""
        fun leading(): Int { order += "first"; return 1 }
        consumeStoredCallback(leading(), ThreadStart(try {
            order += ":second"
            callback
        } catch (_: Exception) { callback }))
        assertEquals("first:second", order)
        assertEquals(3, calls)
    }

    @TestAttribute
    fun conversionWaitsForSuspendedOperandAndDoesNotInvokeIt() {
        val source = SuspendedCallbackSource()
        var completed = false
        var failure: Throwable? = null
        var converted: ThreadStart? = null
        val block: suspend () -> Unit = {
            converted = ThreadStart(source.next())
            completed = true
        }
        block.startCoroutine(object : Continuation<Unit> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { failure = result.exceptionOrNull() }
        })
        assertEquals(1, source.reads)
        assertEquals(false, completed)
        var calls = 0
        source.pending!!.resume { calls++ }
        failure?.let { throw it }
        assertEquals(true, completed)
        assertEquals(0, calls)
        converted!!()
        assertEquals(1, calls)
        assertEquals(1, source.reads)
    }

    @TestAttribute
    fun storedFunctionReturnValuesKeepTheirRepresentation() {
        val format: (Int) -> String = { "value=$it" }
        assertEquals("value=11", Transform(format)(11))
        val box: (Int) -> Any? = { it + 1 }
        assertEquals(12, storedGenericResult(box)(11))
        val identity: (String) -> Any? = { it }
        assertEquals("text", storedGenericResult(identity)("text"))
        val property = ::storedPropertyValue
        assertEquals(27, NullaryResult(property)())
    }

    @TestAttribute
    fun storedFunctionCreatesNominalDelegateWithoutInvokingIt() {
        var calls = 0
        val callback: () -> Unit = { calls++ }
        val converted = storedThreadStart(callback)
        assertEquals(0, calls)
        converted()
        converted()
        assertEquals(2, calls)
        boundedThreadStart(callback)()
        assertEquals(3, calls)
    }

    @TestAttribute
    fun conversionEvaluatesOperandOnceAndForwardsArguments() {
        var calls = 0
        val source = StoredCallbackSource { calls++ }
        val converted = ThreadStart(source.next())
        assertEquals(1, source.reads)
        assertEquals(0, calls)
        converted()
        ThreadStart(source.callback)()
        assertEquals(2, calls)
        assertEquals(1, source.reads)
        var seen: Any? = "before"
        val callback: (Any?) -> Unit = { seen = it }
        val post = SendOrPostCallback(callback)
        post(19)
        assertEquals(19, seen)
        post(null)
        assertEquals(null, seen)
        Legacy.Calls = 0
        val legacy = ThreadStart(Legacy.GetAction()::invoke)
        assertEquals(0, Legacy.Calls)
        legacy()
        assertEquals(1, Legacy.Calls)
    }

    @TestAttribute
    fun genericStoredFunctionsPreserveValueAndReferenceArguments() {
        var number = 0
        val increment: (Int) -> Unit = { number += it }
        storedGenericCallback(increment)(7)
        assertEquals(7, number)
        ArityCb1<Int>(increment)(5)
        assertEquals(12, number)
        var text = ""
        val append: (String) -> Unit = { text += it }
        storedGenericCallback(append)("value")
        assertEquals("value", text)
        ArityCb1<String>(append)("!")
        assertEquals("value!", text)
    }
}
