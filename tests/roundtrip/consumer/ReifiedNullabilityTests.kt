import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.reifiednullability.delayed
import roundtrip.reifiednullability.forwarded
import roundtrip.reifiednullability.matches
import roundtrip.reifiednullability.objectDelayed
import roundtrip.reifiednullability.checker
import roundtrip.reifiednullability.suspended
import roundtrip.reifiednullability.secondDelayed
import roundtrip.reifiednullability.secondObjectDelayed
import roundtrip.reifiednullability.secondChecker
import roundtrip.reifiednullability.secondSuspended
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private fun <T> ordinaryForward(value: Any?): Boolean = matches<T>(value)

private fun runReifiedSuspend(block: suspend () -> Boolean): Boolean {
    var outcome: Result<Boolean>? = null
    block.startCoroutine(object : Continuation<Boolean> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<Boolean>) { outcome = result }
    })
    return outcome!!.getOrThrow()
}

class ReifiedNullabilityTests {
    @TestAttribute
    fun nullableTypeArgumentsSurviveDllKlibRoundtrip() {
        val value: Any? = null
        ClassicAssert.IsTrue(matches<String?>(value))
        ClassicAssert.IsTrue(forwarded<Int?>(value))
        ClassicAssert.IsTrue(delayed<Any?>(value))
        ClassicAssert.IsFalse(matches<String>(value))
        ClassicAssert.IsTrue(objectDelayed<String?>(value))
        ClassicAssert.IsTrue(checker<Int?>().matches(value))
        ClassicAssert.IsTrue(runReifiedSuspend(suspended<Any?>(value)))
        ClassicAssert.IsTrue(secondDelayed<String, Int?>(value))
        ClassicAssert.IsTrue(secondObjectDelayed<String, Int?>(value))
        ClassicAssert.IsTrue(secondChecker<String, Int?>().matches(value))
        ClassicAssert.IsTrue(runReifiedSuspend(secondSuspended<String, Int?>(value)))
        ClassicAssert.IsTrue(ordinaryForward<String>("ok"))
        ClassicAssert.IsFalse(ordinaryForward<String?>(value))
    }
}
