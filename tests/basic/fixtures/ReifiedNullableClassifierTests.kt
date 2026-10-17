import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import System.TimeSpan
import System.InvalidCastException

private inline fun <reified T> nullableClassifierMatches(value: Any?): Boolean = value is T
private inline fun <reified T> nullableClassifierForward(value: Any?): Boolean = nullableClassifierMatches<T>(value)
private inline fun <reified T> nullableClassifierAddNull(value: Any?): Boolean = nullableClassifierMatches<T?>(value)
private inline fun <reified T> nullableClassifierSafeCast(value: Any?): T? = value as? T
private inline fun <reified T> nullableClassifierCast(value: Any?): T = value as T
private inline fun <reified T> nullableClassifierDelegate(): (Any?) -> Boolean = { it is T }
private inline fun <reified T> nullableClassifierEffectMatches(noinline next: () -> Any?): Boolean = next() is T
private inline fun <reified T> nullableClassifierEffectSafeCast(noinline next: () -> Any?): T? = next() as? T
private inline fun <reified T> nullableClassifierEffectCast(noinline next: () -> Any?): T = next() as T
private enum class NullableClassifierEnum { Entry }
private class NullableClassifierBox<T>(val value: T)

class ReifiedNullableClassifierTests {
    @TestAttribute
    fun nullableIntegerChecksItsClassifierInsteadOfItsObjectStorage() {
        ClassicAssert.IsTrue(nullableClassifierMatches<Int?>(null))
        ClassicAssert.IsTrue(nullableClassifierMatches<Int?>(42))
        ClassicAssert.IsFalse(nullableClassifierMatches<Int?>("not an integer"))
        ClassicAssert.IsFalse(nullableClassifierMatches<Int?>(Any()))
        ClassicAssert.IsFalse(nullableClassifierMatches<Int?>(42L))
        ClassicAssert.IsFalse(nullableClassifierMatches<Int>(null))
        ClassicAssert.IsTrue(nullableClassifierMatches<Int>(42))
    }

    @TestAttribute
    fun nullableClassifierIsGeneralAcrossValueTypes() {
        ClassicAssert.IsTrue(nullableClassifierMatches<Boolean?>(true))
        ClassicAssert.IsTrue(nullableClassifierMatches<Boolean?>(null))
        ClassicAssert.IsFalse(nullableClassifierMatches<Boolean?>(42))
        ClassicAssert.IsTrue(nullableClassifierMatches<Long?>(42L))
        ClassicAssert.IsFalse(nullableClassifierMatches<Long?>(42))
        ClassicAssert.IsTrue(nullableClassifierMatches<Double?>(1.5))
        ClassicAssert.IsFalse(nullableClassifierMatches<Double?>("1.5"))
        ClassicAssert.IsTrue(nullableClassifierMatches<NullableClassifierEnum?>(NullableClassifierEnum.Entry))
        ClassicAssert.IsTrue(nullableClassifierMatches<NullableClassifierEnum?>(null))
        ClassicAssert.IsFalse(nullableClassifierMatches<NullableClassifierEnum?>(Any()))
        ClassicAssert.IsTrue(nullableClassifierMatches<TimeSpan?>(TimeSpan(0, 0, 5)))
        ClassicAssert.IsTrue(nullableClassifierMatches<TimeSpan?>(null))
        ClassicAssert.IsFalse(nullableClassifierMatches<TimeSpan?>(42))
        ClassicAssert.IsTrue(nullableClassifierMatches<NullableClassifierBox<Int>?>(NullableClassifierBox(42)))
        ClassicAssert.IsFalse(nullableClassifierMatches<NullableClassifierBox<Int>?>(Any()))
        ClassicAssert.IsTrue(nullableClassifierMatches<String?>("ok"))
        ClassicAssert.IsFalse(nullableClassifierMatches<String?>(42))
        ClassicAssert.IsTrue(nullableClassifierMatches<Any?>("ok"))
        ClassicAssert.IsTrue(nullableClassifierMatches<Any?>(42))
    }

    @TestAttribute
    fun forwardedAndLiftedChecksRetainNullableClassifiers() {
        ClassicAssert.IsTrue(nullableClassifierForward<Int?>(null))
        ClassicAssert.IsTrue(nullableClassifierForward<Int?>(42))
        ClassicAssert.IsFalse(nullableClassifierForward<Int?>("wrong"))
        ClassicAssert.IsTrue(nullableClassifierAddNull<Int>(null))
        ClassicAssert.IsTrue(nullableClassifierAddNull<Int>(42))
        ClassicAssert.IsFalse(nullableClassifierAddNull<Int>("wrong"))
        val check = nullableClassifierDelegate<Int?>()
        ClassicAssert.IsTrue(check(null))
        ClassicAssert.IsTrue(check(42))
        ClassicAssert.IsFalse(check("wrong"))
        ClassicAssert.IsFalse(check(Any()))
    }

    @TestAttribute
    fun checkedCastsUseTheSameClassifierAsMembership() {
        ClassicAssert.IsNull(nullableClassifierSafeCast<Int?>("wrong"))
        ClassicAssert.IsNull(nullableClassifierSafeCast<Int?>(Any()))
        ClassicAssert.IsNull(nullableClassifierSafeCast<Int?>(null))
        ClassicAssert.AreEqual(42, nullableClassifierSafeCast<Int?>(42))
        ClassicAssert.IsNull(nullableClassifierSafeCast<Int>("wrong"))
        ClassicAssert.IsNull(nullableClassifierSafeCast<Int>(null))
        ClassicAssert.AreEqual(42, nullableClassifierSafeCast<Int>(42))
        ClassicAssert.IsNull(nullableClassifierCast<Int?>(null))
        ClassicAssert.AreEqual(42, nullableClassifierCast<Int?>(42))
        var rejected = false
        try { nullableClassifierCast<Int?>("wrong") } catch (_: InvalidCastException) { rejected = true }
        ClassicAssert.IsTrue(rejected)
    }

    @TestAttribute
    fun typeChecksAndCastsEvaluateTheirOperandOnce() {
        var count = 0
        var value: Any? = "wrong"
        val next: () -> Any? = { count++; value }
        ClassicAssert.IsFalse(nullableClassifierEffectMatches<Int?>(next))
        ClassicAssert.AreEqual(1, count)
        value = null
        ClassicAssert.IsTrue(nullableClassifierEffectMatches<Int?>(next))
        ClassicAssert.AreEqual(2, count)
        value = 42
        ClassicAssert.IsTrue(nullableClassifierEffectMatches<Int?>(next))
        ClassicAssert.AreEqual(3, count)
        value = "wrong"
        ClassicAssert.IsNull(nullableClassifierEffectSafeCast<Int?>(next))
        ClassicAssert.AreEqual(4, count)
        value = null
        ClassicAssert.IsNull(nullableClassifierEffectSafeCast<Int?>(next))
        ClassicAssert.AreEqual(5, count)
        value = 42
        ClassicAssert.AreEqual(42, nullableClassifierEffectSafeCast<Int?>(next))
        ClassicAssert.AreEqual(6, count)
        value = null
        ClassicAssert.IsNull(nullableClassifierEffectCast<Int?>(next))
        ClassicAssert.AreEqual(7, count)
        value = 42
        ClassicAssert.AreEqual(42, nullableClassifierEffectCast<Int?>(next))
        ClassicAssert.AreEqual(8, count)
        value = "wrong"
        try { nullableClassifierEffectCast<Int?>(next) } catch (_: InvalidCastException) { }
        ClassicAssert.AreEqual(9, count)
    }
}
