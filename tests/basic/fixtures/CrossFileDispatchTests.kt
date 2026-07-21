// Cross-file battery (batch MigM, from cases/m-c1) — an open class + override and a plain class DECLARED in a
// sibling file (CrossFileDeclarations.kt, package migmc) and INSTANTIATED / virtually dispatched here. Migrated onto the
// in-process NUnit suite; the old case's `main` + il_check golden becomes one @TestAttribute method whose
// per-value assert is strictly stronger (typed) than the old stdout diff; every asserted value preserved 1:1
// (see `// <expected>`).
//
// Coverage preserved (old case -> method):
//   m-c1  -> crossFileClassesAndOverride   cross-file class + method call; open-class area() override reached via label()
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import migmc.MigMCPoint
import migmc.MigMRect

class CrossFileDispatchTests {
    @TestAttribute
    fun crossFileClassesAndOverride() {
        val a = MigMCPoint(3, 4)
        val b = MigMCPoint(1, 2)
        val c = a.plus(b)
        assertEquals("(4, 6)", c.describe())        // c = (4, 6)
        assertEquals(25, a.distanceSquared())       // a.d2 = 25
        val r = MigMRect(5, 6)
        assertEquals("rect area=30", r.label())     // rect area=30 (virtual override across the file boundary)
    }
}
