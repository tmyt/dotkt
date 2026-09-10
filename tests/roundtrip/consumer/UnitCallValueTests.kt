import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.AreSame as assertSame
import unitvalues.effect
import unitvalues.reset
import unitvalues.trace
import unitvalues.identity
import unitvalues.Receiver

private fun referencedUnitReturn(): Any = effect("r")

class UnitCallValueRoundtripTests {
    @TestAttribute
    fun referencedVoidCallsYieldUnitInValueContexts() {
        reset()
        val stored: Any = effect("s")
        assertSame(Unit, stored)
        assertSame(Unit, referencedUnitReturn())
        assertSame(Unit, Receiver().write())
        assertSame(Unit, effect("c") as Any)
        assertEquals("srmc", trace)
    }

    @TestAttribute
    fun genericUnitResultsRemainValuesAcrossDlls() {
        reset()
        assertSame(Unit, identity(effect("g")))
        assertSame(Unit, identity(Unit))
        effect("d")
        assertEquals("gd", trace)
    }
}
