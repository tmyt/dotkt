import CSharp14StaticExtensions.*
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CSharp14StaticExtensionTests {
    @TestAttribute
    fun staticExtensionMethodsAndProperties() {
        assertEquals(42, Alpha.Answer())
        assertEquals(84, Beta.Answer())
        assertEquals(3, Alpha.Select(3))
        assertEquals("selected", Alpha.Select("selected"))
        assertEquals("alpha", Alpha.Label)
        Alpha.Mutable = 9
        assertEquals(9, Alpha.Mutable)
        assertEquals("ComparableValue", GenericTarget.TypeName<ComparableValue>())
    }
}
