// Declaration battery (migration batch M1) — user-defined annotations (-> .NET custom attributes) and data-class
// partial `copy()`. Migrates this declaration family of cases/il-* onto the in-process NUnit suite. Each old case's
// `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assert is strictly stronger (typed)
// than the old text diff. Every value the old il_check asserted is preserved 1:1.
//
// Coverage preserved (old case -> method):
//   il-annot   -> userAnnotations    user annotation class applied to a class/fun; the ANNOTATED members run normally
//                                    (visibility-to-reflection is proven by the old case's C# note, not a stdout value)
//   il-copydef -> dataClassCopyPartial COV/C3 data-class copy(field=x) with OTHER fields omitted -> `this.<field>`;
//                                    cross-module Pair/Triple + same-module user data class. Asserted on the fields
//                                    (name-independent) rather than the toString rendering (subject = the copy fill).
//
// Batch-M1 collision rule: every top-level declaration is `M1`-prefixed (Tag -> M1Tag; Widget -> M1Widget;
// helper -> m1Helper; Point -> M1Point3).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-annot : user annotations emitted as .NET custom attributes; annotated members run normally --------------
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION)
annotation class M1Tag(val name: String, val level: Int, val active: Boolean)

@M1Tag("entity", 3, true)
class M1Widget(val id: Int) { fun show() = "widget#$id" }

@M1Tag("helper", 1, false)
fun m1Helper(n: Int) = n * 2

// ---- il-copydef : data-class copy with omitted fields reconstructed as this.<field> ----------------------------
data class M1Point3(val x: Int, val y: Int, val z: Int)

class MigratedM1DeclTests {
    @TestAttribute
    fun userAnnotations() {
        assertEquals("widget#7", M1Widget(7).show())  // widget#7
        assertEquals(42, m1Helper(21))                // 42
    }

    @TestAttribute
    fun dataClassCopyPartial() {
        val pr1 = (1 to 2).copy(second = 20)          // (1, 20) — cross-module Pair, tail field omitted
        assertEquals(1, pr1.first); assertEquals(20, pr1.second)
        val pr2 = (1 to 2).copy(first = 5)            // (5, 2)  — cross-module Pair, lead field omitted
        assertEquals(5, pr2.first); assertEquals(2, pr2.second)
        val t1 = Triple(1, 2, 3).copy(second = 9)     // (1, 9, 3) — cross-module Triple, middle field
        assertEquals(1, t1.first); assertEquals(9, t1.second); assertEquals(3, t1.third)
        val t2 = Triple(1, 2, 3).copy(first = 7, third = 8) // (7, 2, 8) — two provided, middle omitted
        assertEquals(7, t2.first); assertEquals(2, t2.second); assertEquals(8, t2.third)
        val p1 = M1Point3(1, 2, 3).copy(y = 20)       // (x=1, y=20, z=3) — same-module user data class
        assertEquals(1, p1.x); assertEquals(20, p1.y); assertEquals(3, p1.z)
        val p2 = M1Point3(1, 2, 3).copy(x = 9, z = 8) // (x=9, y=2, z=8)
        assertEquals(9, p2.x); assertEquals(2, p2.y); assertEquals(8, p2.z)
    }
}
