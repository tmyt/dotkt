import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class PublicIndexerChild : InheritedIndexers.PublicBase()
class ProtectedIndexerChild : InheritedIndexers.ProtectedBase() {
    fun read(): Int = this[3]
    fun write(value: Int) { this[3] = value }
    fun readOnlyOverload(): Int = this[true]
    fun superRead(): Int = super.get(3)
    fun captured(): () -> Int = { this[3] }
}

open class GenericIndexerMiddle<X, Y>(initial: X) : InheritedIndexers.GenericBase<Y, X>(initial)
class GenericIndexerChild : GenericIndexerMiddle<String, Int>("initial") {
    fun read(): String = this[2]
    fun write(value: String) { this[2] = value }
    fun captured(): () -> String = { this[2] }
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
        assertEquals(101, protectedChild.readOnlyOverload())
        assertEquals(266, protectedChild.superRead())
        val captured = protectedChild.captured()
        assertEquals(266, captured())
        protectedChild.write(281)
        assertEquals(281, protectedChild.read())
        assertEquals(281, captured())
    }

    @TestAttribute
    fun constructedDeclarationFrame() {
        val protectedChild = GenericIndexerChild()
        assertEquals("initial", protectedChild.read())
        val captured = protectedChild.captured()
        assertEquals("initial", captured())
        protectedChild.write("changed")
        assertEquals("changed", protectedChild.read())
        assertEquals("changed", captured())
        val publicChild = PublicGenericIndexerChild()
        assertEquals("initial", publicChild[3])
        publicChild[3] = "public"
        assertEquals("public", publicChild[3])
    }
}
