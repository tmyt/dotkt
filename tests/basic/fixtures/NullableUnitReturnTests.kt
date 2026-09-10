import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame

private fun localOptionalUnit(present: Boolean): Unit? = if (present) Unit else null
private fun localPlainUnit() {}
private fun <T> localUnitIdentity(value: T): T = value
private var nullableUnitFinally = 0
private fun localTryUnit(present: Boolean): Unit? = try {
    if (!present) throw IllegalStateException("absent")
    Unit
} catch (error: IllegalStateException) {
    null
} finally {
    nullableUnitFinally++
}

class NullableUnitReturnTests {
    @TestAttribute
    fun nullableResultsPreserveBothValuesAndFinally() {
        assertSame(Unit, localOptionalUnit(true))
        assertEquals(null, localOptionalUnit(false))
        nullableUnitFinally = 0
        assertSame(Unit, localTryUnit(true))
        assertEquals(null, localTryUnit(false))
        assertEquals(2, nullableUnitFinally)
    }

    @TestAttribute
    fun nullableFunctionValuesRemainValueReturningDelegates() {
        val reference: (Boolean) -> Unit? = ::localOptionalUnit
        val lambda: (Boolean) -> Unit? = { present -> if (present) Unit else null }
        assertSame(Unit, reference(true))
        assertEquals(null, reference(false))
        assertSame(Unit, lambda(true))
        assertEquals(null, lambda(false))
    }

    @TestAttribute
    fun clrConstructorsAcceptValueReturningUnitDelegates() {
        val absent = System.Threading.ThreadLocal<Unit?>({ null })
        val present = System.Threading.ThreadLocal<Unit?>({ Unit })
        try {
            assertEquals(null, absent.Value)
            assertSame(Unit, present.Value)
        } finally {
            absent.Dispose()
            present.Dispose()
        }
    }

    @TestAttribute
    fun ordinaryUnitAndGenericValuesKeepTheirContracts() {
        val action: () -> Unit = ::localPlainUnit
        assertSame(Unit, action())
        assertSame(Unit, localUnitIdentity(Unit))
        assertSame(Unit, localUnitIdentity<Unit?>(Unit))
        assertEquals(null, localUnitIdentity<Unit?>(null))
    }
}
