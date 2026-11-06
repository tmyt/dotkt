package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.enumidentity.BaseIdentity

class EnumBaseIdentityTests {
    @TestAttribute
    fun selectedEnumBaseIdentitySurvivesTheKotlinDllBoundary() {
        for (value in BaseIdentity.values()) {
            assertTrue(value.baseEquals(value))
            assertFalse(value.baseEquals(null))
            assertEquals(value.hashCode(), value.baseHash())
        }
        assertFalse(BaseIdentity.FIRST.baseEquals(BaseIdentity.SECOND))
        assertFalse(BaseIdentity.ENTRY.baseEquals(BaseIdentity.FIRST))
        assertEquals("entry", BaseIdentity.ENTRY.label())
        assertEquals("plain", BaseIdentity.FIRST.label())
    }
}
