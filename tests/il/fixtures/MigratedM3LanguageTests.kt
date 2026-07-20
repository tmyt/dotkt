// Migrated il batch M3 — core-language family. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals/assertTrue is strictly stronger (typed) than the old text
// diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments). Ordered
// side-effecting `println`s (the destructuring-forEach) become captured list state asserted directly — the
// STRUCTURE that was the actual subject is unchanged.
//
// Coverage preserved (old case -> method):
//   il-iface      -> interfaceDispatch     interface method dispatch through a Greeter-typed param
//   il-infloopret -> infiniteLoopReturn    #141 value-returning while(true){…return x} (value-type Int + reference String)
//   il-inner      -> innerClassCapture     inner class captures the enclosing instance (base + private tag)
//   il-langfeat   -> languageFeatureSweep  anon fun, infix, tailrec, try/finally fall-through, abstract virtual dispatch, destructuring lambda param
//   il-langtail   -> longTailFeatures      `field` accessor, return-as-expression, lateinit read, when/&& smart-cast
//   il-localclass -> localClassLifting     function-local classes (captures, local data class, loop-declared local class)
//   il-loopjump   -> loopBreakContinue     E-0.5 break/continue/labeled-break inside CFG-lowered while loops
//
// All top-level declarations are M3-prefixed (one project = one namespace, shared with sibling batteries + stdlib).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue

// ---- il-iface : interface method dispatch -----------------------------------------------------------------------
interface M3Greeter { fun greet(): String }
class M3English : M3Greeter { override fun greet(): String = "Hello" }
class M3Japanese : M3Greeter { override fun greet(): String = "Konnichiwa" }
fun m3Shout(g: M3Greeter): String = g.greet()

// ---- il-infloopret : #141 value-returning infinite loop (shared counter drives both returns) --------------------
private var m3InfN = 0
fun m3NextInt(): Int {
    while (true) {
        m3InfN++
        if (m3InfN >= 3) return m3InfN * 10
    }
}
fun m3FirstEven(): String {
    while (true) {
        m3InfN++
        if (m3InfN % 2 == 0) return "ok$m3InfN"
    }
}

// ---- il-inner : inner class captures the enclosing instance -----------------------------------------------------
class M3Outer(val base: Int) {
    private val tag = "T"
    inner class Counter(val step: Int) {
        var n = 0
        fun tick(): Int { n = n + 1; return base + n * step }   // base = outer's property
        fun label(): String = tag + n                            // tag = outer's private property
    }
    fun newCounter(step: Int): Counter = Counter(step)
}

// ---- il-langfeat : anon fun / infix / tailrec / try-finally / abstract virtual dispatch -------------------------
val m3Add = fun(a: Int, b: Int): Int = a + b
infix fun Int.m3Pow(e: Int): Int { var r = 1; var i = 0; while (i < e) { r *= this; i++ }; return r }
tailrec fun m3Fact(n: Int, acc: Int): Int = if (n <= 1) acc else m3Fact(n - 1, acc * n)
fun m3WithFinally(): String { var log = ""; try { log += "t" } finally { log += "f" }; return log }
abstract class M3Shape(val name: String) {
    abstract fun area(): Int
    fun describe(): String = "$name=${area()}"
}
class M3Sq(val s: Int) : M3Shape("sq") { override fun area(): Int = s * s }
class M3Circle(val r: Int) : M3Shape("circle") { override fun area(): Int = 3 * r * r }

// ---- il-langtail : `field` accessor / return-as-expression / lateinit read / smart-cast -------------------------
class M3LtCounter {
    var n: Int = 0
        get() = field
        set(v) { field = v + 1 }
}
class M3LtBox { lateinit var s: String }
fun m3Pick(x: Any): String = when (x) {
    is Int -> "int:" + (x + 1)
    is String -> "str:" + x.length
    else -> "other"
}
fun m3Classify(x: Any): String =
    if (x is Int && x > 10) "big:" + (x - 10) else "small"
fun m3FirstPositive(a: Int, b: Int): Int {
    val x = if (a > 0) a else return b
    return x * 100
}

class MigratedM3LanguageTests {
    @TestAttribute
    fun interfaceDispatch() {
        assertEquals("Hello", m3Shout(M3English()))          // Hello
        assertEquals("Konnichiwa", m3Shout(M3Japanese()))    // Konnichiwa
    }

    @TestAttribute
    fun infiniteLoopReturn() {
        m3InfN = 0
        assertEquals(30, m3NextInt())      // 30  (n reaches 3 -> 3*10)
        assertEquals("ok4", m3FirstEven()) // ok4 (continues from n=3 -> n=4, even)
    }

    @TestAttribute
    fun innerClassCapture() {
        val o = M3Outer(100)
        val c = o.newCounter(10)
        assertEquals(110, c.tick())        // 110
        assertEquals(120, c.tick())        // 120
        assertEquals("T2", c.label())      // T2
        assertEquals(5, M3Outer(0).Counter(5).tick())  // 5 (inner built off an Outer receiver)
    }

    @TestAttribute
    fun languageFeatureSweep() {
        assertEquals(7, m3Add(3, 4))                        // 7
        assertEquals(1024, 2 m3Pow 10)                      // 1024
        assertEquals(120, m3Fact(5, 1))                     // 120
        assertEquals("tf", m3WithFinally())                 // tf
        val sh: M3Shape = M3Circle(2)                       // base-typed -> virtual dispatch
        assertEquals("circle=12", sh.describe())            // circle=12
        assertEquals("sq=25", M3Sq(5).describe())           // sq=25
        val log = mutableListOf<String>()
        listOf(1 to "a", 2 to "b").forEach { (n, s) -> log.add("$n$s") }  // destructuring lambda param
        assertEquals("1a,2b", log.joinToString(","))        // 1a, 2b
    }

    @TestAttribute
    fun longTailFeatures() {
        val c = M3LtCounter(); c.n = 5
        assertEquals(6, c.n)                       // 6 (setter +1, getter via field)
        val box = M3LtBox(); box.s = "hi"
        assertEquals("hi", box.s)                  // hi (lateinit)
        assertEquals("int:42", m3Pick(41))         // int:42
        assertEquals("str:3", m3Pick("abc"))       // str:3
        assertEquals("big:5", m3Classify(15))      // big:5
        assertEquals("small", m3Classify(3))       // small
        assertEquals(700, m3FirstPositive(7, 9))   // 700
        assertEquals(9, m3FirstPositive(-1, 9))    // 9 (return as expr)
    }

    @TestAttribute
    fun localClassLifting() {
        class L(val n: Int) { fun d() = n * 2 }
        assertEquals(10, L(5).d())                 // 10
        assertEquals(42, L(21).d())                // 42

        val k = 100
        class Cap { fun g() = k + 1 }              // captures outer local `k`
        assertEquals(101, Cap().g())               // 101

        data class P(val x: Int, val y: Int)       // local data class
        val p = P(3, 4)
        assertEquals("3,4", "${p.x},${p.y}")       // 3,4
        assertTrue(p == P(3, 4))                   // True

        var total = 0
        for (i in 1..3) { class Row(val v: Int); total += Row(i * 10).v }  // local class in a loop
        assertEquals(60, total)                    // 60
    }

    @TestAttribute
    fun loopBreakContinue() {
        var i = 0
        while (true) { if (i == 3) break; i = i + 1 }
        assertEquals("break at 3", "break at $i")  // 3

        var j = 0; var sumOdd = 0
        while (j < 6) { j = j + 1; if (j % 2 == 0) continue; sumOdd = sumOdd + j }
        assertEquals("sumOdd=9", "sumOdd=$sumOdd")  // 1+3+5 = 9

        var a = 0; var hit = "none"
        outer@ while (a < 3) {
            var b = 0
            while (b < 3) {
                if (a + b == 3) { hit = "$a,$b"; break@outer }
                b = b + 1
            }
            a = a + 1
        }
        assertEquals("outer break at 1,2", "outer break at $hit")  // 1,2
    }
}
