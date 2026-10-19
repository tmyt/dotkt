package roundtrip.basicenumcollision

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private enum class Mode { ON, OFF }

class BasicEnumLocalPriorityTests {
    @TestAttribute
    fun localDeclarationWinsOverBothUnusedPrivateReferenceEnums() {
        assertEquals(1, Mode.OFF.ordinal)
        assertEquals("OFF", Mode.OFF.toString())
        assertEquals(Mode.OFF, Mode.values()[1])
        assertEquals(Mode.OFF, enumValues<Mode>()[1])
        assertEquals(Mode.OFF, Mode.valueOf("OFF"))
    }
}
