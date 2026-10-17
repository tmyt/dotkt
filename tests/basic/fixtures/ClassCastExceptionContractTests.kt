import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

private class CastContractValue
private class CastContractFailure(message: String) : ClassCastException(message)

private fun castContractKotlinFailure(operation: () -> Any?): ClassCastException {
    try { operation() } catch (failure: ClassCastException) { return failure }
    error("operation did not throw ClassCastException")
}

private fun castContractNativeFailure(operation: () -> Any?): System.InvalidCastException {
    try { operation() } catch (failure: System.InvalidCastException) { return failure }
    error("operation did not throw System.InvalidCastException")
}

private inline fun <reified T> castContractChecked(value: Any?): T = value as T
private fun castContractIsNative(value: Any): Boolean = value is System.InvalidCastException
private fun castContractIsKotlin(value: Any): Boolean = value is ClassCastException
private fun castContractSame(left: Any, right: Any): Boolean = left === right

class ClassCastExceptionContractTests {
    @TestAttribute
    fun concreteReferenceAndUnboxFailuresAreCaughtAsKotlinClassCastException() {
        val wrongNumber: Any = "wrong"
        val wrongReference: Any = Any()
        val unbox = castContractKotlinFailure { wrongNumber as Int }
        val reference = castContractKotlinFailure { wrongReference as CastContractValue }
        val array = castContractKotlinFailure { wrongReference as Array<*> }
        assertTrue(castContractIsNative(unbox))
        assertTrue(castContractIsNative(reference))
        assertTrue(castContractIsNative(array))
    }

    @TestAttribute
    fun runtimeClassifierFailuresHaveTheSameNativeExceptionIdentity() {
        val wrongNumber: Any = "wrong"
        val wrongCollection: Any = arrayOf(1)
        assertTrue(castContractIsKotlin(castContractNativeFailure { wrongNumber as Number }))
        assertTrue(castContractIsKotlin(castContractNativeFailure { wrongCollection as List<*> }))
        assertTrue(castContractIsKotlin(castContractNativeFailure { castContractChecked<String>(null) }))
        assertTrue(castContractIsKotlin(castContractNativeFailure { castContractChecked<Number>("wrong") }))
        assertTrue(castContractIsNative(castContractKotlinFailure { castContractChecked<Int>("wrong") }))
    }

    @TestAttribute
    fun constructedAndDerivedExceptionsPreserveIdentityAcrossBothCatchVocabularies() {
        val message = ClassCastException("message")
        val derived = CastContractFailure("derived")
        assertEquals("message", message.message)
        assertEquals("derived", derived.message)
        for (failure in arrayOf(ClassCastException(), message, derived)) {
            assertTrue(castContractIsNative(failure))
            assertTrue(castContractSame(castContractNativeFailure { throw failure }, failure))
            assertTrue(castContractKotlinFailure { throw failure } === failure)
        }
        val native = System.InvalidCastException("native")
        assertTrue(castContractSame(castContractKotlinFailure { throw native }, native))
    }

    @TestAttribute
    fun safeAndSuccessfulCastsKeepTheirResultsAndEvaluateOnce() {
        var calls = 0
        val value = CastContractValue()
        val next: () -> Any = { calls++; value }
        assertTrue((next() as CastContractValue) === value)
        assertEquals(1, calls)
        assertTrue(next() as? String === null)
        assertEquals(2, calls)
        assertTrue(castContractChecked<CastContractValue>(next()) === value)
        assertEquals(3, calls)
    }

    @TestAttribute
    fun castCatchDoesNotMaskAnUnrelatedException() {
        val expected = IllegalStateException("unrelated")
        var observed: Throwable? = null
        try {
            throw expected
        } catch (_: ClassCastException) {
            error("ClassCastException caught an unrelated exception")
        } catch (failure: IllegalStateException) {
            observed = failure
        }
        assertTrue(observed === expected)
    }
}
