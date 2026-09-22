import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class PublicIndexerChild : InheritedIndexers.PublicBase()
class ProtectedIndexerChild : InheritedIndexers.ProtectedBase() {
    fun read(): Int = this[3]
    fun write(value: Int) { this[3] = value }
}

open class GenericIndexerMiddle<X, Y>(initial: X) : InheritedIndexers.GenericBase<Y, X>(initial)
class GenericIndexerChild : GenericIndexerMiddle<String, Int>("initial") {
    fun read(): String = this[2]
    fun write(value: String) { this[2] = value }
}
class PublicGenericIndexerChild : InheritedIndexers.PublicGenericBase<Int, String>("initial")

class InheritedIndexerTests {
    @TestAttribute
    fun publicAndProtectedAccessors() {
        val publicChild = PublicIndexerChild()
        assertEquals(19, publicChild[2])
        publicChild[2] = 37
        assertEquals(37, publicChild[2])
        val protectedChild = ProtectedIndexerChild()
        assertEquals(266, protectedChild.read())
        protectedChild.write(281)
        assertEquals(281, protectedChild.read())
    }

    @TestAttribute
    fun constructedDeclarationFrame() {
        val protectedChild = GenericIndexerChild()
        assertEquals("initial", protectedChild.read())
        protectedChild.write("changed")
        assertEquals("changed", protectedChild.read())
        val publicChild = PublicGenericIndexerChild()
        assertEquals("initial", publicChild[3])
        publicChild[3] = "public"
        assertEquals("public", publicChild[3])
    }
}
