package roundtriptests.nullablegenericsignature

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ArraySegment
import roundtrip.nullablegenericsignature.*

class NullableGenericSignatureTests {
    @TestAttribute
    fun importedNullableOverloadsPreserveSelection() {
        val value = ArraySegment<Int>(arrayOf(7, 8))
        val nullable: ArraySegment<Int>? = value
        assertTrue(echo<Int>(null) == null)
        assertEquals(2, echo(nullable)!!.Count)
        assertEquals(2, choose(value))
        assertEquals(12, choose(nullable))
        assertEquals(-1, choose<Int>(null))
    }

    @TestAttribute
    fun importedOwnerAndMethodFramesRemainDistinct() {
        val owner = SegmentEcho<String>()
        assertTrue(owner.echo(null) == null)
        assertEquals(1, owner.echo(ArraySegment<String>(arrayOf("owner")))!!.Count)
        val bound = owner::echo
        assertTrue(bound(null) == null)
        assertEquals(1, bound(ArraySegment<String>(arrayOf("bound")))!!.Count)
        val unbound = SegmentEcho<String>::echo
        assertTrue(unbound(owner, null) == null)
        assertEquals(1, unbound(owner, ArraySegment<String>(arrayOf("unbound")))!!.Count)
        assertTrue(owner.other<Int>(null) == null)
        assertEquals(2, owner.other<Int>(ArraySegment<Int>(arrayOf(3, 4)))!!.Count)
    }
}
