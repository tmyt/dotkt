import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.ThreadStart
import System.Threading.SendOrPostCallback
import Delegobj.ArityCb1
import Cbk.Transform
import Cbk.GenericResult

private fun storedThreadStart(callback: () -> Unit): ThreadStart = ThreadStart(callback)
private fun <T> storedGenericCallback(callback: (T) -> Unit): ArityCb1<T> = ArityCb1<T>(callback)
private fun <T> storedGenericResult(callback: (T) -> Any?): GenericResult<T> = GenericResult<T>(callback)

private class StoredCallbackSource(val callback: () -> Unit) {
    var reads = 0
    fun next(): () -> Unit {
        reads++
        return callback
    }
}

class StoredDelegateConversionTests {
    @TestAttribute
    fun storedFunctionReturnValuesKeepTheirRepresentation() {
        val format: (Int) -> String = { "value=$it" }
        assertEquals("value=11", Transform(format)(11))
        val box: (Int) -> Any? = { it + 1 }
        assertEquals(12, storedGenericResult(box)(11))
        val identity: (String) -> Any? = { it }
        assertEquals("text", storedGenericResult(identity)("text"))
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
    }

    @TestAttribute
    fun genericStoredFunctionsPreserveValueAndReferenceArguments() {
        var number = 0
        val increment: (Int) -> Unit = { number += it }
        storedGenericCallback(increment)(7)
        assertEquals(7, number)
        var text = ""
        val append: (String) -> Unit = { text += it }
        storedGenericCallback(append)("value")
        assertEquals("value", text)
    }
}
