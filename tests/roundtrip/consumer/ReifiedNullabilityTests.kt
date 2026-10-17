import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import System.InvalidCastException
import System.TimeSpan
import roundtrip.reifiednullability.delayed
import roundtrip.reifiednullability.forwarded
import roundtrip.reifiednullability.matches
import roundtrip.reifiednullability.safeCast
import roundtrip.reifiednullability.checkedCast
import roundtrip.reifiednullability.nullableForwarded
import roundtrip.reifiednullability.classifierName
import roundtrip.reifiednullability.objectDelayed
import roundtrip.reifiednullability.checker
import roundtrip.reifiednullability.suspended
import roundtrip.reifiednullability.secondDelayed
import roundtrip.reifiednullability.secondNonCapturingDelayed
import roundtrip.reifiednullability.splicedCaptured
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

private inline fun <reified T> propertyForward(value: Any?): Boolean =
    ReifiedPropertyCarrier<T>(value).propertyMatches

private inline fun <reified T> splicedForward(value: Any?): Boolean =
    splicedCaptured<T>(value) {}

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
    fun nonNullValuesKeepTheirClassifierAcrossDllAndLiftedFrames() {
        for (value in arrayOf<Any?>(null, 42, "not an integer", Any(), 42L)) {
            val expected = value == null || value is Int
            ClassicAssert.AreEqual(expected, matches<Int?>(value))
            ClassicAssert.AreEqual(expected, forwarded<Int?>(value))
            ClassicAssert.AreEqual(expected, nullableForwarded<Int>(value))
            ClassicAssert.AreEqual(expected, delayed<Int?>(value))
            ClassicAssert.AreEqual(expected, objectDelayed<Int?>(value))
            ClassicAssert.AreEqual(expected, checker<Int?>().matches(value))
            ClassicAssert.AreEqual(expected, runReifiedSuspend(suspended<Int?>(value)))
            ClassicAssert.AreEqual(expected, secondDelayed<String, Int?>(value))
            ClassicAssert.AreEqual(expected, secondObjectDelayed<String, Int?>(value))
            ClassicAssert.AreEqual(expected, secondChecker<String, Int?>().matches(value))
            ClassicAssert.AreEqual(expected, runReifiedSuspend(secondSuspended<String, Int?>(value)))
            ClassicAssert.AreEqual(expected, splicedForward<Int?>(value))
            ClassicAssert.AreEqual(expected, ReifiedPropertyCarrier<Int?>(value).propertyMatches)
            ClassicAssert.AreEqual(expected, propertyForward<Int?>(value))
        }
        ClassicAssert.IsTrue(matches<Boolean?>(true))
        ClassicAssert.IsFalse(matches<Boolean?>(42))
        ClassicAssert.IsTrue(forwarded<Long?>(42L))
        ClassicAssert.IsFalse(forwarded<Long?>(42))
        ClassicAssert.IsTrue(forwarded<TimeSpan?>(TimeSpan(0, 0, 5)))
        ClassicAssert.IsTrue(checker<TimeSpan?>().matches(null))
        ClassicAssert.IsFalse(checker<TimeSpan?>().matches(42))
        ClassicAssert.IsNull(safeCast<Int?>("wrong"))
        ClassicAssert.IsNull(safeCast<Int?>(Any()))
        ClassicAssert.AreEqual(42, safeCast<Int?>(42))
        ClassicAssert.IsNull(safeCast<Int>("wrong"))
        ClassicAssert.IsNull(safeCast<Int>(null))
        ClassicAssert.AreEqual(42, safeCast<Int>(42))
        ClassicAssert.IsNull(checkedCast<Int?>(null))
        ClassicAssert.AreEqual(42, checkedCast<Int?>(42))
        var rejected = false
        try { checkedCast<Int?>("wrong") } catch (_: InvalidCastException) { rejected = true }
        ClassicAssert.IsTrue(rejected)
    }

    @TestAttribute
    fun nullableTypeArgumentsSurviveDllKlibRoundtrip() {
        val value: Any? = null
        ClassicAssert.IsTrue(matches<String?>(value))
        ClassicAssert.AreEqual("String", classifierName<String>())
        ClassicAssert.IsTrue(forwarded<Int?>(value))
        ClassicAssert.IsTrue(delayed<Any?>(value))
        ClassicAssert.IsFalse(matches<String>(value))
        ClassicAssert.IsTrue(objectDelayed<String?>(value))
        ClassicAssert.IsTrue(checker<Int?>().matches(value))
        ClassicAssert.IsTrue(runReifiedSuspend(suspended<Any?>(value)))
        ClassicAssert.IsTrue(secondDelayed<String, Int?>(value))
        ClassicAssert.IsTrue(secondNonCapturingDelayed<String, Int?>()())
        ClassicAssert.IsFalse(secondNonCapturingDelayed<String, Int>()())
        ClassicAssert.IsTrue(splicedCaptured<String?>(value) {})
        ClassicAssert.IsFalse(splicedCaptured<String>(value) {})
        ClassicAssert.IsTrue(splicedForward<String?>(value))
        ClassicAssert.IsFalse(splicedForward<String>(value))
        ClassicAssert.IsTrue(secondObjectDelayed<String, Int?>(value))
        ClassicAssert.IsTrue(secondChecker<String, Int?>().matches(value))
        ClassicAssert.IsTrue(runReifiedSuspend(secondSuspended<String, Int?>(value)))
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
