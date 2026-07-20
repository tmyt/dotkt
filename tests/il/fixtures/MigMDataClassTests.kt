// Data-class battery (batch MigM, from cases/m-s2) — the synthesized `data class` surface: toString, copy(named),
// componentN destructuring accessors, structural `==`, and hashCode consistency. Migrated onto the in-process
// NUnit suite; each old case's `main` + golden becomes one @TestAttribute method whose per-value assert is
// strictly stronger (typed — the boolean `==`/hashCode results are asserted as real Booleans, not the CLR-native
// "True"/"False" text the old stdout diff carried) than the old stdout diff; every asserted value preserved 1:1
// (see `// <expected>`). Formerly DOUBLE-registered (verify-il il_check `dataq` AND verify-differential PURE
// `m-s2`) — both registrations removed in this same change.
//
// Coverage preserved (old case -> method):
//   m-s2  -> dataClassMembers   toString / copy(named args) / component1/component2 / structural == / hashCode
//
// Top-level names are MigM-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse

data class MigMPoint(val x: Int, val y: Int)

class MigMDataClassTests {
    @TestAttribute
    fun dataClassMembers() {
        val p = MigMPoint(3, 4)
        assertEquals("MigMPoint(x=3, y=4)", p.toString())   // MigMPoint(x=3, y=4)
        val q = p.copy(x = 7, y = 9)
        assertEquals("MigMPoint(x=7, y=9)", q.toString())   // MigMPoint(x=7, y=9)  (copy with named args)
        assertEquals(3, p.component1())                     // 3  (destructuring accessor)
        assertEquals(4, p.component2())                     // 4
        val a = MigMPoint(1, 2)
        val b = MigMPoint(1, 2)
        val c = MigMPoint(3, 4)
        assertTrue(a == b)                                  // a==b: true  (structural equality)
        assertFalse(a == c)                                 // a==c: false
        assertTrue(a.hashCode() == b.hashCode())            // hash eq: true (consistent with ==)
    }
}
