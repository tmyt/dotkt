// IN-PROCESS control for the cross-module round-trip finding: the SAME Shape/Rect/Square hierarchy compiled
// and run in ONE assembly (no re-import). If this passes here but fails in the roundtrip consumer, the gap is
// specifically facadegen re-import losing a subclass override of an interface DEFAULT method (bir2cir/facadegen
// re-import), not an ilemit virtual-dispatch bug.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

interface Shape2 {
    fun area(): Int
    fun describe(): String = "shape area=" + area()
}
open class Rect2(val w: Int, val h: Int) : Shape2 {
    override fun area(): Int = w * h
}
class Square2(side: Int) : Rect2(side, side) {
    override fun describe(): String = "square area=" + area()
}

// Direct-child override of an interface default method (to pin the bug boundary vs the grandchild case).
open class Circle2(val r: Int) : Shape2 {
    override fun area(): Int = 3 * r * r
    override fun describe(): String = "circle area=" + area()
}

class InterfaceDispatchTests {
    // WORKING axes (kept green): interface ABSTRACT-method override dispatch (area), and the un-overridden
    // interface DEFAULT method (Rect2.describe inherits the default).
    @TestAttribute
    fun abstractOverrideAndInheritedDefaultDispatch() {
        val s: Shape2 = Square2(4)
        ClassicAssert.AreEqual(16, s.area())          // Shape2.area (abstract) -> Rect2 override, through Square2
        val r: Shape2 = Rect2(2, 3)
        ClassicAssert.AreEqual("shape area=6", r.describe())   // inherited interface default
    }

    // Direct-child override of an interface DEFAULT method — WORKS.
    @TestAttribute
    fun directChildOverrideOfInterfaceDefault() {
        val c: Shape2 = Circle2(2)
        ClassicAssert.AreEqual("circle area=12", c.describe())
    }
}
