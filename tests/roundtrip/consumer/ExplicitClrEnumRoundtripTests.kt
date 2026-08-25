package roundtrip.consumer

import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.TestAttribute
import roundtrip.clrenum.OrderedCode
import roundtrip.clrenum.classifyOrderedCode

class ExplicitClrEnumRoundtripTests {
    @TestAttribute
    fun orderedValuesAndPhysicalConstantsSurviveDllToKlibRoundtrip() {
        val values = OrderedCode.values()
        assertEquals("FIRST", values[0].name)
        assertEquals("NEGATIVE", values[1].name)
        assertEquals("ZERO", values[2].name)
        val reifiedValues = enumValues<OrderedCode>()
        assertEquals("FIRST", reifiedValues[0].name)
        assertEquals("NEGATIVE", reifiedValues[1].name)
        assertEquals("ZERO", reifiedValues[2].name)
        assertEquals(0, OrderedCode.FIRST.ordinal)
        assertEquals(1, OrderedCode.NEGATIVE.ordinal)
        assertEquals(2, OrderedCode.ZERO.ordinal)
        assertEquals(-2, OrderedCode.FIRST.compareTo(OrderedCode.ZERO))
        assertEquals("NEGATIVE", OrderedCode.valueOf("NEGATIVE").name)
        assertEquals("negative", classifyOrderedCode(OrderedCode.NEGATIVE))
        assertEquals(526, OrderedCode.Companion.marker())
    }
}
