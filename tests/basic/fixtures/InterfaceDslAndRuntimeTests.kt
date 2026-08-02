// Language-core battery (feature fixture) — migrates a spread of pure-language cases/il-* onto the in-process NUnit suite.
// Each old case's `main` + stdout-golden diff becomes one @TestAttribute method with typed asserts; every asserted
// value is preserved 1:1. Ordered side-effecting `println`s (exprbody's Unit-evaluation proof) become captured
// log-list state asserted directly — the STRUCTURE that was the subject is unchanged. Top-level declarations are
// `InterfaceDsl`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-dimgrandchild -> interfaceDsl_dimgrandchild  #185 interface DIM overridden by a GRANDCHILD (intermediate does not) must
//                                          dispatch the override (per-type MethodImpl); + GetEnumerator-via-base twin
//   il-dsl           -> interfaceDsl_dsl            receiver lambdas (Scope.()->Unit) + nested receiver lambda + outer capture
//   il-exprbody      -> interfaceDsl_exprbody       expr-body fn whose body expression is Unit-typed must EVALUATE then return
//   il-for           -> interfaceDsl_for            for over `1..n` and `n downTo 1`
//   il-getclass      -> interfaceDsl_getclass       `x::class.simpleName` -> x.GetType().Name
//   il-equalscall    -> interfaceDsl_equalscall     §5a explicit `.equals()` -> total-order (Double/Float) / structural (colls)
//   il-fmt           -> interfaceDsl_fmt            String.format is a thin bind to System.String.Format (.NET composite fmt)
//   il-duration      -> interfaceDsl_duration       kotlin.time Duration: companion ext-prop accessors carry the receiver;
//                                          value-class member operators emit as real method calls; negative toString
//
// Renamed types carry an incidental name into their output: `Square/Circle/...`->`InterfaceDsl*` (describe strings updated),
// `Widget`->`InterfaceDslWidget` (::class.simpleName reads the CLR name). The subject (dispatch / runtime class recovery) is
// unchanged. il-fmt is CLR-specific-by-design (.NET composite format strings, never in the JVM differential PURE
// set); il-duration was JVM-differential PURE — both land here as typed value asserts.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.time.Duration.Companion.milliseconds
import kotlin.time.Duration.Companion.seconds

// ---- il-dimgrandchild : #185 interface DIM overridden by a grandchild; intermediate does not override ------------
interface InterfaceDslDescribable {
    val area: Int
    fun describe(): String = "shape area=$area"     // interface default method (DIM)
}
open class InterfaceDslShape(override val area: Int) : InterfaceDslDescribable        // does NOT override describe() — inherits the DIM
class InterfaceDslSquare(side: Int) : InterfaceDslShape(side * side) {
    override fun describe() = "square area=$area"                 // grandchild override, intermediate did not override
}
class InterfaceDslCircle(override val area: Int) : InterfaceDslDescribable {
    override fun describe() = "circle area=$area"                 // direct child overriding the DIM (non-regression)
}
open class InterfaceDslPoly(area: Int) : InterfaceDslShape(area)
class InterfaceDslPentagon(area: Int) : InterfaceDslPoly(area) { override fun describe() = "pentagon area=$area" }  // deeper base chain
abstract class InterfaceDslNumberBag(val nums: List<Int>) : Iterable<Int>  // abstract base implements Iterable, iterator() abstract
class InterfaceDslOrderedBag(nums: List<Int>) : InterfaceDslNumberBag(nums) {
    override fun iterator(): Iterator<Int> = nums.iterator()      // subclass reaches Iterable only via its base
}

// ---- il-dsl : receiver lambdas + nested receiver lambda + capture across them ------------------------------------
class InterfaceDslCol {
    var s = ""
    fun text(t: String) { s = s + t }
    fun row(block: InterfaceDslCol.() -> Unit) { val c = InterfaceDslCol(); c.block(); s = s + "[" + c.s + "]" }
}
fun interfaceDslColumn(block: InterfaceDslCol.() -> Unit): InterfaceDslCol { val c = InterfaceDslCol(); c.block(); return c }

// ---- il-exprbody : expr-body fn whose body is a Unit-typed side-effecting call (must evaluate, not drop) ---------
val interfaceDslExprbodyLog = mutableListOf<String>()
fun interfaceDslLog(s: String) { interfaceDslExprbodyLog.add(s) }                     // Unit-returning side-effecting call (was println)
fun interfaceDslApp(block: () -> Unit) { block() }
fun interfaceDslCleanup() { interfaceDslLog("cleanup") }
fun interfaceDslGreetE() = interfaceDslLog("greet")                                   // expr-body, direct Unit call
fun interfaceDslViaLambda() = interfaceDslApp { interfaceDslLog("viaLambda") }                  // expr-body, Unit call taking a lambda
fun interfaceDslCond(x: Int) { if (x < 0) return interfaceDslCleanup(); interfaceDslLog("pos") } // explicit `return <Unit expr>`
fun interfaceDslRunE(block: () -> Unit) = block()

// ---- il-getclass : `x::class.simpleName` -> x.GetType().Name ----------------------------------------------------
class InterfaceDslWidget
fun interfaceDslDescribe(x: Any): String = x::class.simpleName ?: "?"

// ---- il-fmt : String.format -> System.String.Format (.NET composite format strings, non-literal template) -------
fun interfaceDslFmtLine(n: Int, pct: Double, label: String): String {
    val tmpl = "{0} items, {1:F1}% ({2})"                         // non-literal (variable) format string
    return String.format(tmpl, n, pct, label)
}

class InterfaceDefaultDispatchTests {
    @TestAttribute
    fun dimgrandchild() {
        val s: InterfaceDslShape = InterfaceDslSquare(4)
        assertEquals("square area=16", s.describe())     // square area=16   (grandchild override, base-typed)
        val d: InterfaceDslDescribable = InterfaceDslSquare(3)
        assertEquals("square area=9", d.describe())      // square area=9    (interface-typed dispatch)
        val plain: InterfaceDslShape = InterfaceDslShape(7)
        assertEquals("shape area=7", plain.describe())   // shape area=7     (still the DIM default)
        val c: InterfaceDslDescribable = InterfaceDslCircle(5)
        assertEquals("circle area=5", c.describe())      // circle area=5    (direct child, non-regression)
        val p: InterfaceDslDescribable = InterfaceDslPentagon(11)
        assertEquals("pentagon area=11", p.describe())   // pentagon area=11 (deeper base-class chain)
        var sum = 0
        for (x in InterfaceDslOrderedBag(listOf(4, 5, 6))) sum += x
        assertEquals(15, sum)                            // 15  (Iterable via the abstract base; GetEnumerator on subclass)
    }

}

class ReceiverLambdaAndUnitExpressionTests {
    @TestAttribute
    fun dsl() {
        val prefix = "P"
        val r = interfaceDslColumn {
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
    fun exprbody() {
        interfaceDslExprbodyLog.clear()
        interfaceDslRunE {
            interfaceDslGreetE()
            interfaceDslViaLambda()
            interfaceDslCond(-1)   // x < 0 -> return interfaceDslCleanup() (explicit return of a Unit expr)
            interfaceDslCond(1)    // else -> interfaceDslLog("pos")
        }
        assertEquals("greet", interfaceDslExprbodyLog[0])      // greet
        assertEquals("viaLambda", interfaceDslExprbodyLog[1])  // viaLambda
        assertEquals("cleanup", interfaceDslExprbodyLog[2])    // cleanup
        assertEquals("pos", interfaceDslExprbodyLog[3])        // pos
        assertEquals(4, interfaceDslExprbodyLog.size)          // all four Unit exprs evaluated (none silently dropped)
    }

}

class IterationAndRuntimeTypeTests {
    @TestAttribute
    fun rangeIteration() {
        var s = 0
        for (i in 1..5) { s = s + i }
        assertEquals(15, s)          // sum 1..5 = 15
        var out = ""
        for (i in 5 downTo 1) { out = out + i }
        assertEquals("54321", out)   // countdown 5 = 54321
    }

    @TestAttribute
    fun runtimeClassNames() {
        assertEquals("String", "hi"::class.simpleName)   // String
        val w = InterfaceDslWidget()
        assertEquals("InterfaceDslWidget", w::class.simpleName)    // InterfaceDslWidget (was Widget)
        assertEquals("InterfaceDslWidget", interfaceDslDescribe(w))          // InterfaceDslWidget (w passed as Any, runtime class recovered)
        assertEquals("String", interfaceDslDescribe("text"))       // String
    }

}

class EqualityFormattingAndDurationTests {
    @TestAttribute
    fun equalscall() {
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
    fun fmt() {
        assertEquals("42 items, 87.5% (ok)", interfaceDslFmtLine(42, 87.5, "ok"))   // 42 items, 87.5% (ok)
        assertEquals("00007-ff", String.format("{0:D5}-{1:x}", 7, 255))   // 00007-ff
        assertEquals("[a   ]", String.format("[{0,-4}]", "a"))            // [a   ]
        assertEquals("[bb  ]", String.format("[{0,-4}]", "bb"))           // [bb  ]
    }

    @TestAttribute
    fun duration() {
        val d = 2.seconds + 3.seconds
        assertEquals("5s", d.toString())                                       // 5s
        assertEquals("2s", (1500.milliseconds + 500.milliseconds).toString())  // 2s  (carry across units)
        assertEquals("-1s", (-(1.seconds)).toString())                         // -1s (unaryMinus + negative toString)
        assertTrue((2.seconds - 3.seconds).isNegative())                       // True
    }
}
