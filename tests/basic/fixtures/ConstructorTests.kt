// Constructor / constructor-reference battery (feature fixture) — migrates the object-construction family of
// cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals is strictly stronger (typed) than the old text diff. Every
// value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-ctor    -> secondaryCtors_initBlocks   secondary constructors + init blocks, delegating via this(...)
//   il-ctorref -> ctorReferences              `::Ctor` refs (stored / as HOF arg) + a user-type-returning lambda,
//                                             resolved via TypeBuilder.GetConstructor/GetMethod
//
// Assembly-wide collision rule (one battery assembly = one namespace): every top-level declaration is `Constructor`-prefixed so it
// cannot clash with a sibling fixture (e.g. il-ctorref's `Point` -> `ConstructorPointR`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- il-ctor : secondary constructors + init blocks; secondary delegates to primary via this(...) --------------
class ConstructorRect(val w: Int, val h: Int) {
    var area: Int = 0
    init { area = w * h }
    constructor(side: Int) : this(side, side)
}
class ConstructorLabeled {
    val label: String
    val n: Int
    constructor(label: String, n: Int) {
        this.label = label
        this.n = n
    }
    constructor(label: String) : this(label, 0)
}

// ---- il-ctorref : `::Ctor` references + user-type-returning lambda through Func<…, UserType> ---------------------
class ConstructorPointR(val x: Int, val y: Int) { fun show(): String = "($x,$y)" }
fun constructorBuild(f: (Int, Int) -> ConstructorPointR): ConstructorPointR = f(3, 4)
fun constructorMakeWith(f: (Int) -> ConstructorPointR): String = f(9).show()

class ConstructorTests {
    @TestAttribute
    fun initBlocks() {
        val r = ConstructorRect(3, 4)
        assertEquals(12, r.area)                        // 12  (init block: w*h)
        val sq = ConstructorRect(5)
        assertEquals(25, sq.area)                       // 25  (secondary this(5,5) -> init)
        assertEquals("5x5", "${sq.w}x${sq.h}")          // 5x5
        val a = ConstructorLabeled("hi", 7)
        assertEquals("hi=7", "${a.label}=${a.n}")       // hi=7
        val b = ConstructorLabeled("solo")
        assertEquals("solo=0", "${b.label}=${b.n}")     // solo=0 (secondary delegates with n=0)
    }

    @TestAttribute
    fun ctorReferences() {
        val mk = ::ConstructorPointR                             // constructor reference stored in a val
        assertEquals("(1,2)", mk(1, 2).show())          // (1,2)
        assertEquals("(3,4)", constructorBuild(::ConstructorPointR).show()) // (3,4)  (::Ctor as a higher-order arg)
        assertEquals("(9,9)", constructorMakeWith { n -> ConstructorPointR(n, n) }) // (9,9)  (lambda returning a user type)
    }
}
