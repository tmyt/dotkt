package roundtriptests.physicalnrtpositions

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.physicalnrtpositions.*

class PhysicalNrtPositionsTests {
    @TestAttribute
    fun importedOrdinarySlotsKeepNonNullFollowingArguments() {
        val value: String = collapsedNonNull().second
        assertEquals("value", value)
        val slots = NrtSlots(collapsedNonNull())
        val fromField: String = slots.fieldSlot.second
        val fromProperty: String = slots.propertySlot.second
        val fromCall: String = echoCollapsed(slots.propertySlot).second
        assertEquals(value, fromField)
        assertEquals(value, fromProperty)
        assertEquals(value, fromCall)
    }

    @TestAttribute
    fun importedNullableAndNestedSlotsKeepTheirAnnotations() {
        assertTrue(collapsedNullable().second == null)
        assertTrue(collapsedHead() == null)
        val slots = NrtSlots(collapsedNonNull())
        assertTrue(slots.nullableFieldSlot.second == null)
        assertTrue(slots.nullablePropertySlot.second == null)
        val nested = nestedCollapsed()
        val inner: String = nested.first.second
        assertEquals("value", inner)
        assertTrue(nested.second == null)
        val retained: String = retainedGeneric().second
        assertEquals("retained", retained)
    }
}
