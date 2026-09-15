package roundtriptests.physicalnrtpositions

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.physicalnrtpositions.*

private fun <T> useExchange(contract: NrtExchange<T>): String {
    val value = NrtTriple<T?, Comparable<Any?>?, String>(null, null, "override")
    val result: String = contract.exchange(value).third
    return result
}

class PhysicalNrtPositionsTests {
    @TestAttribute
    fun importedRewrittenOverrideKeepsFollowingArgumentNonNull() {
        val implementation = StringNrtExchange()
        val contract: NrtExchange<String> = implementation
        val throughInterface: String = useExchange(contract)
        assertEquals("override", throughInterface)
    }

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
