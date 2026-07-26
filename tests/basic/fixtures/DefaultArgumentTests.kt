// Default-argument battery (M2 batch) — migrates the default-argument family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals is strictly stronger (typed) than the old text diff; every asserted value is preserved 1:1
// (see the `// <expected>` comments). Top-level declarations are `M2`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-defargs  -> m2_defargs   C3: cross+same-module default args; an omitted middle default must not shift a
//                                later provided arg's slot (joinToString / substringAfter `= this` / copy(field=))
//   il-defargs2 -> m2_defargs2  same-module default referencing ANOTHER value param (`b = a*10`, `c = a+b`)
//
// The data class was `P`; renamed `M2P` for collision-freedom, so its toString reads `M2P(...)` — the class name is
// incidental to the subject (default-arg slot correctness), which is unchanged.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

data class M2P(val x: Int, val y: Int, val z: Int)

fun m2Greet(name: String, greeting: String = "Hello", punct: String = "!"): String = "$greeting, $name$punct"

fun m2F(a: Int, b: Int = a * 10): Int = a + b
fun m2G(x: Int, y: Int = x + 1): Int = x * y
fun m2H(a: Int, b: Int = 3, c: Int = a + b): Int = a * 100 + b * 10 + c

// #235: the CONSTRUCTOR shapes of the same defaults — a default that reads an EARLIER constructor parameter, in a
// plain class, a data class, reading TWO earlier params, with a later arg passed by name, and on a secondary ctor.
// The INNER class and the LOCAL class (m2LocalDefault) additionally cover the arg positions a `new` PREPENDS in front
// of the filled args — the enclosing instance and the lifted captures — which must not shift the filled slots.
class M2Rect(val w: Int, val h: Int = w * 2)
data class M2DRect(val w: Int, val h: Int = w * 3, val tag: String = "d")
class M2Tri(val a: Int, val b: Int = a + 1, val c: Int = a * 10 + b)
class M2Sec(val v: Int) {
    constructor(a: Int, b: Int, c: Int = a * 5) : this(a + b + c)
}
class M2Own(val n: Int) {
    inner class M2In(val a: Int, val b: Int = a * 2) {
        val all: Int get() = n * 100 + a * 10 + b
    }
}

fun m2LocalDefault(seed: Int): Int {
    class L(val p: Int, val q: Int = p + seed)
    return L(2).q
}

// #235: the constructor call sites that are NOT a `new` — a `: this(…)` / `: super(…)` delegation and an enum
// entry's `NAME(args)` (both the plain-entry form and a per-entry BODY's base call). Each is an omitting call site,
// so an omitted default must be filled there too, not dropped (dropping it slides every later arg one slot down).
class M2Del(val a: Int, val b: Int = a * 2) {
    constructor() : this(3)
}
open class M2DelBase(val a: Int, val b: Int = a + 1)
class M2DelSub : M2DelBase(2)

enum class M2Hue(val rgb: Int, val label: String = "c") {
    R(1),
    G(2, "g")
}

enum class M2Op(val arity: Int, val label: String = "op") {
    ADD(2) { override fun apply(a: Int, b: Int): Int = a + b },
    NEG(1, "neg") { override fun apply(a: Int, b: Int): Int = -a };

    abstract fun apply(a: Int, b: Int): Int
}

// #235: a constructor default that reads the ENCLOSING instance — one level up (the call's own dispatch receiver)
// and two (through that level's `__outer`).
class M2Encl(val k: Int) {
    inner class M2EIn(val x: Int = k * 4)
}
class M2Deep(val q: Int) {
    inner class M2DMid {
        inner class M2DIn(val x: Int = q + 1)
    }
}

// #235: a closure captures the callee's OWN parameter and a recursive call inside it omits a default that reads
// that same parameter — filling must not clobber the closure's capture binding for it.
fun m2Rec(a: Int, b: Int = a * 2): Int {
    if (a <= 0) return b
    val f = { a + m2Rec(a - 1) }
    return f()
}

class DefaultArgumentTests {
    @TestAttribute
    fun defargs() {
        val xs = listOf(1, 2, 3)
        assertEquals("x1-x2-x3", xs.joinToString("-") { "x$it" })                          // x1-x2-x3
        assertEquals("1, 2, 3", xs.joinToString())                                         // 1, 2, 3
        assertEquals("[1, 2, 3]", xs.joinToString(prefix = "[", postfix = "]"))            // [1, 2, 3]
        assertEquals("1/2/~", xs.joinToString(separator = "/", limit = 2, truncated = "~")) // 1/2/~
        assertEquals("b=c", "a=b=c".substringAfter("="))                                   // b=c
        assertEquals("a", "a=b=c".substringBefore("="))                                    // a
        assertEquals("nodelim", "nodelim".substringAfter("="))                             // nodelim
        assertEquals("FALLBACK", "nodelim".substringBefore("=", "FALLBACK"))               // FALLBACK
        val p = M2P(1, 2, 3)
        assertEquals("M2P(x=1, y=20, z=3)", p.copy(y = 20).toString())                     // was P(x=1, y=20, z=3)
        assertEquals("M2P(x=10, y=2, z=30)", p.copy(x = 10, z = 30).toString())            // was P(x=10, y=2, z=30)
        assertEquals("Hello, Kotlin!", m2Greet("Kotlin"))                                  // Hello, Kotlin!
        assertEquals("Hello, Kotlin?", m2Greet("Kotlin", punct = "?"))                     // Hello, Kotlin?
    }

    @TestAttribute
    fun defargs2() {
        assertEquals(55, m2F(5))        // 55
        assertEquals(7, m2F(5, 2))      // 7
        assertEquals(12, m2G(3))        // 12
        assertEquals(30, m2G(3, 10))    // 30
        assertEquals(134, m2H(1))       // 134
        assertEquals(156, m2H(1, 5))    // 156
        assertEquals(159, m2H(1, 5, 9)) // 159
    }

    // #235: a constructor default that reads an earlier constructor parameter is filled at the omitting `new`,
    // exactly as the function-path defaults above are — one default-filling pass serves both.
    @TestAttribute
    fun defargsCtor() {
        assertEquals(6, M2Rect(3).h)                                    // w * 2
        assertEquals(5, M2Rect(3, 5).h)                                 // provided, not defaulted
        assertEquals("M2DRect(w=2, h=6, tag=d)", M2DRect(2).toString())
        assertEquals("M2DRect(w=2, h=6, tag=z)", M2DRect(2, tag = "z").toString())   // later arg by NAME keeps its slot
        assertEquals("M2DRect(w=4, h=6, tag=d)", M2DRect(2).copy(w = 4).toString())  // copy's own self-defaults still fill
        val t = M2Tri(2)
        assertEquals(3, t.b)                                            // a + 1
        assertEquals(23, t.c)                                           // a * 10 + b, reading TWO earlier params
        val t2 = M2Tri(2, 10)
        assertEquals(30, t2.c)                                          // a * 10 + b, b provided
        assertEquals(8, M2Sec(1, 2).v)                                  // secondary ctor: c = a * 5 -> 1 + 2 + 5
        assertEquals(6, M2Sec(1, 2, 3).v)                               // secondary ctor, c provided
        assertEquals(136, M2Own(1).M2In(3).all)                         // inner: enclosing instance prepended, b = a * 2
        assertEquals(134, M2Own(1).M2In(3, 4).all)                      // inner, b provided
        assertEquals(12, m2LocalDefault(10))                            // local class: captures prepended, q = p + seed
    }

    // #235: the constructor call sites that used to bypass default filling entirely — a delegation and an enum entry.
    @TestAttribute
    fun defargsCtorDelegation() {
        assertEquals(3, M2Del().a)                                      // `: this(3)`
        assertEquals(6, M2Del().b)                                      // omitted there: a * 2
        assertEquals(12, M2Del(4, 12).b)                                // the primary ctor still takes both
        assertEquals(2, M2DelSub().a)                                   // `: super(2)`
        assertEquals(3, M2DelSub().b)                                   // omitted there: a + 1
        assertEquals("c", M2Hue.R.label)                                // enum entry `R(1)` omits label
        assertEquals(1, M2Hue.R.rgb)
        assertEquals("g", M2Hue.G.label)                                // provided
        assertEquals("op", M2Op.ADD.label)                              // entry BODY: the subclass's base() call omits it
        assertEquals(5, M2Op.ADD.apply(2, 3))
        assertEquals("neg", M2Op.NEG.label)                             // entry BODY, provided
        assertEquals(-2, M2Op.NEG.apply(2, 3))
    }

    // #235: a constructor default reading the enclosing instance, and one reading a parameter a closure captured.
    @TestAttribute
    fun defargsCtorEnclosingInstance() {
        assertEquals(20, M2Encl(5).M2EIn().x)                           // k * 4, k from the enclosing instance
        assertEquals(1, M2Encl(5).M2EIn(1).x)                           // provided
        assertEquals(2, M2Deep(1).M2DMid().M2DIn().x)                   // q + 1, two levels up (via `__outer`)
        assertEquals(9, M2Deep(1).M2DMid().M2DIn(9).x)                  // provided
        assertEquals(6, m2Rec(3))                                       // 3 + (2 + (1 + 0))
        assertEquals(0, m2Rec(0))                                       // b = a * 2 = 0
        assertEquals(1, m2Rec(1))
    }
}
