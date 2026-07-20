// Constructor / constructor-reference battery (migration batch M1) — migrates the object-construction family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals is strictly stronger (typed) than the old text diff. Every
// value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-ctor    -> secondaryCtors_initBlocks   secondary constructors + init blocks, delegating via this(...)
//   il-ctorref -> ctorReferences              `::Ctor` refs (stored / as HOF arg) + a user-type-returning lambda,
//                                             resolved via TypeBuilder.GetConstructor/GetMethod
//
// Batch-M1 collision rule (one battery assembly = one namespace): every top-level declaration is `M1`-prefixed so it
// cannot clash with a sibling batch's fixture (e.g. il-ctorref's `Point` -> `M1PointR`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-ctor : secondary constructors + init blocks; secondary delegates to primary via this(...) --------------
class M1Rect(val w: Int, val h: Int) {
    var area: Int = 0
    init { area = w * h }
    constructor(side: Int) : this(side, side)
}
class M1Labeled {
    val label: String
    val n: Int
    constructor(label: String, n: Int) {
        this.label = label
        this.n = n
    }
    constructor(label: String) : this(label, 0)
}

// ---- il-ctorref : `::Ctor` references + user-type-returning lambda through Func<…, UserType> ---------------------
class M1PointR(val x: Int, val y: Int) { fun show(): String = "($x,$y)" }
fun m1Build(f: (Int, Int) -> M1PointR): M1PointR = f(3, 4)
fun m1MakeWith(f: (Int) -> M1PointR): String = f(9).show()

class MigratedM1CtorTests {
    @TestAttribute
    fun secondaryCtors_initBlocks() {
        val r = M1Rect(3, 4)
        assertEquals(12, r.area)                        // 12  (init block: w*h)
        val sq = M1Rect(5)
        assertEquals(25, sq.area)                       // 25  (secondary this(5,5) -> init)
        assertEquals("5x5", "${sq.w}x${sq.h}")          // 5x5
        val a = M1Labeled("hi", 7)
        assertEquals("hi=7", "${a.label}=${a.n}")       // hi=7
        val b = M1Labeled("solo")
        assertEquals("solo=0", "${b.label}=${b.n}")     // solo=0 (secondary delegates with n=0)
    }

    @TestAttribute
    fun ctorReferences() {
        val mk = ::M1PointR                             // constructor reference stored in a val
        assertEquals("(1,2)", mk(1, 2).show())          // (1,2)
        assertEquals("(3,4)", m1Build(::M1PointR).show()) // (3,4)  (::Ctor as a higher-order arg)
        assertEquals("(9,9)", m1MakeWith { n -> M1PointR(n, n) }) // (9,9)  (lambda returning a user type)
    }
}
