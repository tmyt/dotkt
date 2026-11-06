package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.enumidentity.BaseIdentity

class EnumBaseIdentityTests {
    private fun assertSelectedBase(value: BaseIdentity) {
        assertTrue(value.baseEquals(value))
        assertFalse(value.baseEquals(null))
        assertEquals(value.hashCode(), value.baseHash())
    }

    @TestAttribute
    fun selectedEnumBaseIdentitySurvivesTheKotlinDllBoundary() {
        assertSelectedBase(BaseIdentity.FIRST)
        assertSelectedBase(BaseIdentity.SECOND)
        assertSelectedBase(BaseIdentity.ENTRY)
        assertFalse(BaseIdentity.FIRST.baseEquals(BaseIdentity.SECOND))
        assertFalse(BaseIdentity.ENTRY.baseEquals(BaseIdentity.FIRST))
        assertEquals("entry", BaseIdentity.ENTRY.label())
        assertEquals("plain", BaseIdentity.FIRST.label())
    }
}
