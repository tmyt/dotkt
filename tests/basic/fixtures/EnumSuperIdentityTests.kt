import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import System.DayOfWeek

private enum class EnumSuperIdentity(val code: Int) {
    FIRST(7), SECOND(7), ENTRY(7) { override fun label(): String = "entry" };

    open fun label(): String = "plain"
    fun baseEquals(other: Any?): Boolean = super.equals(other)
    fun baseHash(): Int = super.hashCode()
}

private enum class EnumSuperBasic { FIRST, SECOND }

private fun <E : Enum<E>> enumSuperHashReference(value: E): Int {
    val hash = value::hashCode
    return hash()
}

private fun <E : Enum<E>> enumSuperEqualsReference(value: E, other: Any?): Boolean {
    val equals = value::equals
    return equals(other)
}

class EnumSuperIdentityTests {
    @TestAttribute
    fun selectedBaseIdentityPreservesRichEntriesAndNativeValueControls() {
        for (value in EnumSuperIdentity.values()) {
            val hash = value.baseHash()
            assertTrue(value.baseEquals(value))
            assertFalse(value.baseEquals(null))
            assertFalse(value.baseEquals(EnumSuperBasic.FIRST))
            assertEquals(value.hashCode(), hash)
            assertEquals(hash, value.baseHash())
            assertEquals(hash, enumSuperHashReference(value))
            assertTrue(enumSuperEqualsReference(value, value))
        }
        assertFalse(EnumSuperIdentity.FIRST.baseEquals(EnumSuperIdentity.SECOND))
        assertFalse(EnumSuperIdentity.ENTRY.baseEquals(EnumSuperIdentity.FIRST))
        val boxedBasic: Any = EnumSuperBasic.FIRST
        assertTrue(enumSuperEqualsReference(EnumSuperBasic.FIRST, boxedBasic))
        assertFalse(enumSuperEqualsReference(EnumSuperBasic.FIRST, EnumSuperBasic.SECOND))
        assertEquals(EnumSuperBasic.FIRST.hashCode(), enumSuperHashReference(EnumSuperBasic.FIRST))
        val boxedNative: Any = DayOfWeek.Monday
        assertTrue(enumSuperEqualsReference(DayOfWeek.Monday, boxedNative))
        assertFalse(enumSuperEqualsReference(DayOfWeek.Monday, EnumSuperBasic.SECOND))
        assertFalse(enumSuperEqualsReference(DayOfWeek.Monday, DayOfWeek.Tuesday))
        assertEquals(DayOfWeek.Monday.hashCode(), enumSuperHashReference(DayOfWeek.Monday))
    }
}
