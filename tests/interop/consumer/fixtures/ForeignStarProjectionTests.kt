import ForeignStar.Box
import ForeignStar.Factory
import ForeignStar.IBox
import ForeignStar.Pair
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

class ForeignStarProjectionTests {
    @TestAttribute
    fun arbitraryClrGenericUsesRuntimeClassifierAndExactMember() {
        val value: Any = Factory.StringBoxAsObject()
        assertTrue(value is Box<*>)
        assertFalse(("not a box" as Any) is Box<*>)
        assertTrue((value as? Box<*>) === value)
        assertTrue((null as Any?) is Box<*>?)

        val box = value as Box<*>
        assertEquals("foreign", box.Read())
        assertEquals("int:4", box.Describe(4))
        assertEquals("string:x", box.Describe("x"))
        assertEquals("Int32:9", box.EchoType<Int>(9))
        try {
            box.Throwing()
            throw IllegalStateException("expected foreign exception")
        } catch (failure: Throwable) {
            assertEquals("foreign-boom", failure.message)
        }
    }

    @TestAttribute
    fun interfaceAndMixedProjectionPreserveIdentityAndReadableSlots() {
        val exact = Factory.StringBox()
        val projected: IBox<*> = exact
        assertTrue(projected === exact)
        assertEquals("foreign", projected.Read())

        val pair: Pair<*, String> = Factory.Pair()
        assertEquals(7, pair.First)
        assertEquals("seven", pair.Second)
        pair.Second = "property-changed"
        assertEquals("property-changed", pair.Second)
        assertEquals(7, pair.FirstField)
        pair.SecondField = "changed"
        assertEquals("changed", pair.SecondField)
    }
}
