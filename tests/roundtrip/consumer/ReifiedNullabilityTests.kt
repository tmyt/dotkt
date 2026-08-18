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
import roundtrip.reifiednullability.ReifiedPropertyCarrier
import roundtrip.reifiednullability.ReifiedPropertyContext
import roundtrip.reifiednullability.SecondReifiedPropertyCarrier
import roundtrip.reifiednullability.contextPropertyMatches
import roundtrip.reifiednullability.propertyMatches
import roundtrip.reifiednullability.reifiedPropertyValue
import roundtrip.reifiednullability.secondPropertyMatches
import roundtrip.reifiednullability.ordinaryPropertyValue
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private fun <T> ordinaryForward(value: Any?): Boolean = matches<T>(value)

private inline fun <reified T> propertyForward(value: Any?): Boolean =
    ReifiedPropertyCarrier<T>(value).propertyMatches

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
        ClassicAssert.IsTrue(ReifiedPropertyCarrier<String?>(value).propertyMatches)
        ClassicAssert.IsFalse(ReifiedPropertyCarrier<String>(value).propertyMatches)
        ClassicAssert.IsTrue(propertyForward<Int?>(value))
        with(ReifiedPropertyContext(value)) {
            ClassicAssert.IsTrue(ReifiedPropertyCarrier<String?>(value).contextPropertyMatches)
            ClassicAssert.IsFalse(ReifiedPropertyCarrier<String>(value).contextPropertyMatches)
        }
        ClassicAssert.AreEqual("ok", ReifiedPropertyCarrier<String>("ok").ordinaryPropertyValue)
        ClassicAssert.AreEqual(41, ReifiedPropertyCarrier<Int>(41).ordinaryPropertyValue)
        val propertyCarrier = ReifiedPropertyCarrier<String?>(null)
        ClassicAssert.IsNull(propertyCarrier.reifiedPropertyValue)
        propertyCarrier.reifiedPropertyValue = "updated"
        ClassicAssert.AreEqual("updated", propertyCarrier.reifiedPropertyValue)
        val nullableIntPropertyCarrier = ReifiedPropertyCarrier<Int?>(null)
        ClassicAssert.IsTrue(nullableIntPropertyCarrier.propertyMatches)
        ClassicAssert.IsNull(nullableIntPropertyCarrier.reifiedPropertyValue)
        nullableIntPropertyCarrier.reifiedPropertyValue = 42
        ClassicAssert.AreEqual(42, nullableIntPropertyCarrier.reifiedPropertyValue)
        ClassicAssert.IsFalse(ReifiedPropertyCarrier<Int>(null).propertyMatches)
        ClassicAssert.IsTrue(SecondReifiedPropertyCarrier<String, Int?>(null).secondPropertyMatches)
        ClassicAssert.IsFalse(SecondReifiedPropertyCarrier<String, Int>(null).secondPropertyMatches)
    }
}
