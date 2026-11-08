package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import roundtrip.enumidentity.BaseIdentity

class EnumBaseIdentityTests {
    @TestAttribute
    fun importedRichEnumValuesCarryTheirArrayElementType() {
        var direct = 0
        for (value in BaseIdentity.values()) direct += value.code
        assertEquals(21, direct)
        var intrinsic = 0
        for (value in enumValues<BaseIdentity>()) intrinsic += value.code
        assertEquals(21, intrinsic)
        var visited = 0
        for (value in BaseIdentity.values()) {
            if (value == BaseIdentity.SECOND) continue
            visited++
            if (value == BaseIdentity.ENTRY) break
        }
        assertEquals(2, visited)
    }

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
