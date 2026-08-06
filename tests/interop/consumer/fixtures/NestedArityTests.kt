import NestedArityInterop.Outer
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class NestedArityTests {
    @TestAttribute
    fun nestedClassifiersThatDifferOnlyByArityRemainDistinct() {
        assertEquals(1, Outer.Item().Value)
        assertEquals("generic", Outer.Item1("generic").Value)
    }
}
