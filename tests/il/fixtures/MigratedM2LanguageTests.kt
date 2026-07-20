// Language-core battery (M2 batch) — migrates a spread of pure-language cases/il-* onto the in-process NUnit suite.
// Each old case's `main` + stdout-golden diff becomes one @TestAttribute method with typed asserts; every asserted
// value is preserved 1:1. Ordered side-effecting `println`s (exprbody's Unit-evaluation proof) become captured
// log-list state asserted directly — the STRUCTURE that was the subject is unchanged. Top-level declarations are
// `M2`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-dimgrandchild -> m2_dimgrandchild  #185 interface DIM overridden by a GRANDCHILD (intermediate does not) must
//                                          dispatch the override (per-type MethodImpl); + GetEnumerator-via-base twin
//   il-dsl           -> m2_dsl            receiver lambdas (Scope.()->Unit) + nested receiver lambda + outer capture
//   il-exprbody      -> m2_exprbody       expr-body fn whose body expression is Unit-typed must EVALUATE then return
//   il-for           -> m2_for            for over `1..n` and `n downTo 1`
//   il-getclass      -> m2_getclass       `x::class.simpleName` -> x.GetType().Name
//   il-equalscall    -> m2_equalscall     §5a explicit `.equals()` -> total-order (Double/Float) / structural (colls)
//   il-fmt           -> m2_fmt            String.format is a thin bind to System.String.Format (.NET composite fmt)
//   il-duration      -> m2_duration       kotlin.time Duration: companion ext-prop accessors carry the receiver;
//                                          value-class member operators emit as real method calls; negative toString
//
// Renamed types carry an incidental name into their output: `Square/Circle/...`->`M2*` (describe strings updated),
// `Widget`->`M2Widget` (::class.simpleName reads the CLR name). The subject (dispatch / runtime class recovery) is
// unchanged. il-fmt is CLR-specific-by-design (.NET composite format strings, never in the JVM differential PURE
// set); il-duration was JVM-differential PURE — both land here as typed value asserts.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

// ---- il-dimgrandchild : #185 interface DIM overridden by a grandchild; intermediate does not override ------------
interface M2Describable {
    val area: Int
    fun describe(): String = "shape area=$area"     // interface default method (DIM)
}
open class M2Shape(override val area: Int) : M2Describable        // does NOT override describe() — inherits the DIM
class M2Square(side: Int) : M2Shape(side * side) {
    override fun describe() = "square area=$area"                 // grandchild override, intermediate did not override
}
class M2Circle(override val area: Int) : M2Describable {
    override fun describe() = "circle area=$area"                 // direct child overriding the DIM (non-regression)
}
open class M2Poly(area: Int) : M2Shape(area)
class M2Pentagon(area: Int) : M2Poly(area) { override fun describe() = "pentagon area=$area" }  // deeper base chain
abstract class M2NumberBag(val nums: List<Int>) : Iterable<Int>  // abstract base implements Iterable, iterator() abstract
class M2OrderedBag(nums: List<Int>) : M2NumberBag(nums) {
    override fun iterator(): Iterator<Int> = nums.iterator()      // subclass reaches Iterable only via its base
}

// ---- il-dsl : receiver lambdas + nested receiver lambda + capture across them ------------------------------------
class M2Col {
    var s = ""
    fun text(t: String) { s = s + t }
    fun row(block: M2Col.() -> Unit) { val c = M2Col(); c.block(); s = s + "[" + c.s + "]" }
}
fun m2Column(block: M2Col.() -> Unit): M2Col { val c = M2Col(); c.block(); return c }

// ---- il-exprbody : expr-body fn whose body is a Unit-typed side-effecting call (must evaluate, not drop) ---------
val m2ExprbodyLog = mutableListOf<String>()
fun m2Log(s: String) { m2ExprbodyLog.add(s) }                     // Unit-returning side-effecting call (was println)
fun m2App(block: () -> Unit) { block() }
fun m2Cleanup() { m2Log("cleanup") }
fun m2GreetE() = m2Log("greet")                                   // expr-body, direct Unit call
fun m2ViaLambda() = m2App { m2Log("viaLambda") }                  // expr-body, Unit call taking a lambda
fun m2Cond(x: Int) { if (x < 0) return m2Cleanup(); m2Log("pos") } // explicit `return <Unit expr>`
fun m2RunE(block: () -> Unit) = block()

// ---- il-getclass : `x::class.simpleName` -> x.GetType().Name ----------------------------------------------------
class M2Widget
fun m2Describe(x: Any): String = x::class.simpleName ?: "?"

// ---- il-fmt : String.format -> System.String.Format (.NET composite format strings, non-literal template) -------
fun m2FmtLine(n: Int, pct: Double, label: String): String {
    val tmpl = "{0} items, {1:F1}% ({2})"                         // non-literal (variable) format string
    return String.format(tmpl, n, pct, label)
}

class MigratedM2LanguageTests {
    @TestAttribute
    fun m2_dimgrandchild() {
        val s: M2Shape = M2Square(4)
        assertEquals("square area=16", s.describe())     // square area=16   (grandchild override, base-typed)
        val d: M2Describable = M2Square(3)
        assertEquals("square area=9", d.describe())      // square area=9    (interface-typed dispatch)
        val plain: M2Shape = M2Shape(7)
        assertEquals("shape area=7", plain.describe())   // shape area=7     (still the DIM default)
        val c: M2Describable = M2Circle(5)
        assertEquals("circle area=5", c.describe())      // circle area=5    (direct child, non-regression)
        val p: M2Describable = M2Pentagon(11)
        assertEquals("pentagon area=11", p.describe())   // pentagon area=11 (deeper base-class chain)
        var sum = 0
        for (x in M2OrderedBag(listOf(4, 5, 6))) sum += x
        assertEquals(15, sum)                            // 15  (Iterable via the abstract base; GetEnumerator on subclass)
    }

    @TestAttribute
    fun m2_dsl() {
        val prefix = "P"
        val r = m2Column {
            text("a")
            row {
                text(prefix)     // captures the outer `prefix` across a nested receiver lambda
                text("b")
            }
            text("c")
        }
        assertEquals("a[Pb]c", r.s)   // a[Pb]c
    }

    @TestAttribute
    fun m2_exprbody() {
        m2ExprbodyLog.clear()
        m2RunE {
            m2GreetE()
            m2ViaLambda()
            m2Cond(-1)   // x < 0 -> return m2Cleanup() (explicit return of a Unit expr)
            m2Cond(1)    // else -> m2Log("pos")
        }
        assertEquals("greet", m2ExprbodyLog[0])      // greet
        assertEquals("viaLambda", m2ExprbodyLog[1])  // viaLambda
        assertEquals("cleanup", m2ExprbodyLog[2])    // cleanup
        assertEquals("pos", m2ExprbodyLog[3])        // pos
        assertEquals(4, m2ExprbodyLog.size)          // all four Unit exprs evaluated (none silently dropped)
    }

    @TestAttribute
    fun m2_for() {
        var s = 0
        for (i in 1..5) { s = s + i }
        assertEquals(15, s)          // sum 1..5 = 15
        var out = ""
        for (i in 5 downTo 1) { out = out + i }
        assertEquals("54321", out)   // countdown 5 = 54321
    }

    @TestAttribute
    fun m2_getclass() {
        assertEquals("String", "hi"::class.simpleName)   // String
        val w = M2Widget()
        assertEquals("M2Widget", w::class.simpleName)    // M2Widget (was Widget)
        assertEquals("M2Widget", m2Describe(w))          // M2Widget (w passed as Any, runtime class recovered)
        assertEquals("String", m2Describe("text"))       // String
    }

    @TestAttribute
    fun m2_equalscall() {
        assertFalse((-0.0).equals(0.0))                  // false — total order (-0.0 != 0.0)
        assertTrue(0.0.equals(0.0))                      // true
        assertTrue(Double.NaN.equals(Double.NaN))        // true  — NaN == NaN structurally
        assertFalse((-0.0f).equals(0.0f))                // false — Float total order
        assertTrue(1.5.equals(1.5))                      // true
        assertTrue(listOf(1, 2).equals(listOf(1, 2)))    // true  — List structural (ordered)
        assertFalse(listOf(1, 2).equals(listOf(2, 1)))   // false
        assertTrue(setOf(1, 2).equals(setOf(2, 1)))      // true  — Set structural (unordered)
        assertTrue(mapOf(1 to 2).equals(mapOf(1 to 2)))  // true  — Map structural (entrywise)
        val a: Any = Any(); val b: Any = Any()
        assertFalse(a.equals(b))                         // false — plain object reference identity
        assertTrue(a.equals(a))                          // true
        assertTrue("hi".equals("hi"))                    // true  — String value equality
    }

    @TestAttribute
    fun m2_fmt() {
        assertEquals("42 items, 87.5% (ok)", m2FmtLine(42, 87.5, "ok"))   // 42 items, 87.5% (ok)
        assertEquals("00007-ff", String.format("{0:D5}-{1:x}", 7, 255))   // 00007-ff
        assertEquals("[a   ]", String.format("[{0,-4}]", "a"))            // [a   ]
        assertEquals("[bb  ]", String.format("[{0,-4}]", "bb"))           // [bb  ]
    }

    @TestAttribute
    fun m2_duration() {
        val d = 2.seconds + 3.seconds
        assertEquals("5s", d.toString())                                       // 5s
        assertEquals("2s", (1500.milliseconds + 500.milliseconds).toString())  // 2s  (carry across units)
        assertEquals("-1s", (-(1.seconds)).toString())                         // -1s (unaryMinus + negative toString)
        assertTrue((2.seconds - 3.seconds).isNegative())                       // True
    }
}
