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

// #235: the filled expression is bound BY SYMBOL, so an argument that itself reads `this` (`c.m(k)` passes `this.k`)
// keeps its own receiver instead of being re-pointed at the call's receiver. M2SelfArg.k and M2SelfHolder.k differ so
// a wrong receiver is a wrong VALUE, not just a wrong type.
class M2SelfArg(val k: Int) {
    fun m(a: Int, b: Int = a * 10): Int = a * 1000 + b
}
class M2SelfHolder {
    val k = 5
    val c = M2SelfArg(7)
    fun call(): Int = c.m(k)
}

// #235: the enclosing instance reached through a NON-`this` receiver (`o.M2EIn()` inside another class), and an inner
// class's own secondary constructor delegating with the target's default reading the enclosing instance.
class M2EnclHolder(val o: M2Encl) {
    fun x(): Int = o.M2EIn().x
}
class M2EnclDel(val k: Int) {
    inner class In(val a: Int, val b: Int = k) {
        constructor() : this(1)
    }
}

// #235: a MEMBER function (not a constructor) of an inner class, whose default reads the enclosing instance.
class M2EnclMember(val k: Int) {
    inner class Q {
        fun f(x: Int = k * 4): Int = x
    }
}

// A callee with TWO receivers: a default's `this` read binds to the receiver of ITS OWN KIND. A member EXTENSION
// whose default reads the DISPATCH receiver (`this@Owner.k`) had every receiver param bound to one collapsed
// expression — the EXTENSION receiver — so the default read an Owner member off the extension receiver's VALUE and
// reached CIL as `M2RecvKind.get_k()` on an Int: a NullReferenceException at runtime with nothing loud at compile
// time. The values differ so a wrong receiver is a wrong VALUE, not merely a wrong type.
class M2RecvKind(val k: Int) {
    fun Int.scaledV(f: Int = k): Int = this * f
    inline fun Int.scaledI(f: Int = k): Int = this * f
    fun Int.viaParam(base: Int, f: Int = base): Int = this * f
    fun run(): Int = 3.scaledV()
    fun runInline(): Int = 3.scaledI()
    fun runParam(): Int = 3.viaParam(7)
}

// The same collapse reached the ENCLOSING-this chain: an inner class's member EXTENSION whose default reads the
// OUTER class's `this@Outer` had that chain hang off the extension receiver instead of its own dispatch receiver.
class M2RecvOuter(val k: Int) {
    inner class R {
        fun Int.viaOuter(f: Int = k): Int = this * f
        fun run(): Int = 3.viaOuter()
    }
}

// A TOP-LEVEL extension whose default reads the extension receiver — the sound single-receiver arm, pinned so the
// kind-directed binding cannot regress it.
fun Int.m2SelfScaled(f: Int = this): Int = this * f

// #235: a lifted LOCAL class whose secondary constructor delegates — the capture params are leading args of the
// TARGET constructor too, and the delegation's own omitted default reads the capture.
fun m2LocalDelegate(seed: Int): Int {
    class L(val p: Int, val q: Int = seed) {
        constructor() : this(1)
    }
    return L().q
}

// #235: SINGLE EVALUATION of a value a filled default splices. Every counter lives on a per-test instance (no shared
// top-level state) so the suite can run these in parallel.
fun m2SideF(a: Int, b: Int = a * 10): Int = a + b
class M2SideC(val a: Int, val b: Int = a * 10) { val sum: Int get() = a + b }
class M2SideHolder(val s: String) {
    fun tag(t: String = s): String = s + "/" + t
}
class M2EvalOnce {
    var calls = 0
    fun next(): Int { calls++; return 4 }
    fun holder(): M2SideHolder { calls++; return M2SideHolder("h") }
    fun encl(): M2Encl { calls++; return M2Encl(9) }
}

// #235: an OMITTED default is a value too. If a later omitted default reads it, the first default must be evaluated
// once and shared, just like an explicitly provided side-effecting argument.
class M2DefaultSource {
    var calls = 0
    fun bump(): Int { calls++; return 3 }
}
fun m2DefaultChainF(source: M2DefaultSource, a: Int = source.bump(), b: Int = a * 10): Int = a * 1000 + b
class M2DefaultChainC(val source: M2DefaultSource, val a: Int = source.bump(), val b: Int = a * 10)
class M2DefaultChainDel(val source: M2DefaultSource, val a: Int = source.bump(), val b: Int = a * 10) {
    constructor(source: M2DefaultSource, unused: String) : this(source)
}
object M2DefaultEnumSource {
    var calls = 0
    fun bump(): Int { calls++; return 3 }
}
enum class M2DefaultChainE(val a: Int = M2DefaultEnumSource.bump(), val b: Int = a * 10) {
    ONLY
}

// #235: the two call sites that are not expressions — a constructor DELEGATION and an ENUM ENTRY. Their arguments ride a
// declaration rather than an expression, so their single-evaluation temps are declared by the first argument; a value a
// filled default reads must still run exactly once. The counter is a per-test instance except for the enum, whose entries
// are initialized ONCE per process by the static initializer — so those two read a companion counter.
class M2DelOnce(val p: Int, val q: Int = p * 10) {
    constructor(unused: String) : this(M2DelCounter.next())
}
open class M2DelBaseOnce(val p: Int, val q: Int = p * 10)
class M2DelSubOnce : M2DelBaseOnce(M2DelCounter.next())
object M2DelCounter {
    var calls = 0
    fun next(): Int { calls++; return 4 }
}
// No entry bodies (the entry-field path) and ALL entry bodies (the per-entry subclass's base call) are separate enums:
// an enum MIXING the two is unloadable for reasons unrelated to default arguments (it reproduces with none).
enum class M2EnumOnce(val n: Int, val m: Int = n * 10) {
    R(M2EnumCounter.next()),
    G(M2EnumCounter.next())
}
enum class M2EnumBodyOnce(val n: Int, val m: Int = n * 10) {
    X(M2EnumBodyCounter.next()) { override fun tag(): String = "x" },
    Y(M2EnumBodyCounter.next()) { override fun tag(): String = "y" };

    abstract fun tag(): String
}
object M2EnumCounter { var calls = 0; fun next(): Int { calls++; return 4 } }
object M2EnumBodyCounter { var calls = 0; fun next(): Int { calls++; return 4 } }

// #235: EVALUATION ORDER around a value bound for single evaluation. Binding one value moves its evaluation ahead of
// the call, so every side-effecting value to its LEFT must move with it — Kotlin evaluates the receiver, then each
// argument, left to right.
fun m2Order(p: Int, q: Int, r: Int = q * 10): Int = p * 10000 + q * 100 + r
class M2Log {
    var s = ""
    fun a(): Int { s += "a"; return 1 }
    fun b(): Int { s += "b"; return 2 }
    fun self(): M2Log { s += "m"; return this }
    fun m(p: Int, q: Int = p * 10): Int = p * 100 + q
}
class M2Cell {
    var x = 0
    fun bump(): Int { x = 5; return 1 }
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
        assertEquals(5050, M2SelfHolder().call())                       // a = this.k = 5, b = 50 (NOT c.k = 7)
        assertEquals(36, M2EnclHolder(M2Encl(9)).x())                   // k * 4 = 36, enclosing instance through `o`, not `this`
        assertEquals(7, M2EnclDel(7).In().b)                            // inner secondary ctor delegating
        assertEquals(2, M2EnclDel(7).In(1, 2).b)                        // provided
        assertEquals(20, M2EnclMember(5).Q().f())                       // MEMBER of an inner class: k * 4
        assertEquals(1, M2EnclMember(5).Q().f(1))                       // provided
        assertEquals(11, m2LocalDelegate(11))                           // local class, secondary ctor delegating
    }

    // #235: a value a filled default SPLICES is evaluated exactly once — Kotlin evaluates a receiver and each
    // argument once, and the splice must not re-run it per reading default.
    @TestAttribute
    fun defargsSingleEval() {
        val a = M2EvalOnce()
        assertEquals(44, m2SideF(a.next()))                             // 4 + 40
        assertEquals(1, a.calls)                                        // the argument ran ONCE, not once per splice

        val b = M2EvalOnce()
        assertEquals(44, M2SideC(b.next()).sum)                         // the same through a `new`
        assertEquals(1, b.calls)

        val c = M2EvalOnce()
        assertEquals(23, M2Tri(c.next() - 2).c)                         // a = 2, read by BOTH b and c defaults
        assertEquals(1, c.calls)

        val d = M2EvalOnce()
        assertEquals("h/h", d.holder().tag())                           // the RECEIVER a `= s` default reads
        assertEquals(1, d.calls)

        val e = M2EvalOnce()
        assertEquals(36, e.encl().M2EIn().x)                            // the ENCLOSING INSTANCE a `= k * 4` default reads
        assertEquals(1, e.calls)                                        // ctor and default see the SAME instance

        val f = M2EvalOnce()
        assertEquals(5, m2SideF(f.next(), 1))                           // no default filled -> no temp, still once
        assertEquals(1, f.calls)

        val g = M2DefaultSource()
        assertEquals(3030, m2DefaultChainF(g))                           // a = bump(), b = a * 10
        assertEquals(1, g.calls)                                        // the FILLED default itself ran once

        val h = M2DefaultSource()
        val hc = M2DefaultChainC(h)
        assertEquals(3, hc.a)
        assertEquals(30, hc.b)
        assertEquals(1, h.calls)                                        // the same rule through a constructor `new`

        val i = M2DefaultSource()
        val ic = M2DefaultChainDel(i, "")
        assertEquals(3, ic.a)
        assertEquals(30, ic.b)
        assertEquals(1, i.calls)                                        // and through a constructor delegation

        assertEquals(3, M2DefaultChainE.ONLY.a)
        assertEquals(30, M2DefaultChainE.ONLY.b)
        assertEquals(1, M2DefaultEnumSource.calls)                       // and through an enum-entry initializer
    }

    // #235: binding a value for single evaluation must not REORDER the call's other values.
    @TestAttribute
    fun defargsEvalOrder() {
        val l = M2Log()
        assertEquals(10220, m2Order(l.a(), l.b()))                      // p=1, q=2, r=q*10=20
        assertEquals("ab", l.s)                                         // q is bound; p must still run FIRST

        val l2 = M2Log()
        assertEquals(110, l2.self().m(l2.a()))                          // p=1, q=p*10=10
        assertEquals("ma", l2.s)                                        // receiver before argument

        val c = M2Cell()
        assertEquals(10550, m2Order(c.bump(), c.x))                     // p=1, then q reads x=5, r=50
    }

    // #235: single evaluation at the two call sites that ride a DECLARATION rather than an expression.
    @TestAttribute
    fun defargsSingleEvalDelegationAndEnum() {
        M2DelCounter.calls = 0
        val d = M2DelOnce("")
        assertEquals(4, d.p)                                            // `: this(next())`
        assertEquals(40, d.q)                                           // p * 10, filled at the delegation
        assertEquals(1, M2DelCounter.calls)                             // next() ran ONCE, not once per splice

        M2DelCounter.calls = 0
        val s = M2DelSubOnce()
        assertEquals(4, s.p)                                            // `: super(next())`
        assertEquals(40, s.q)
        assertEquals(1, M2DelCounter.calls)

        // The entries are constructed by the enum's static initializer, so each counter reflects that ONE construction:
        // two entries, one `next()` each.
        assertEquals(4, M2EnumOnce.R.n)
        assertEquals(40, M2EnumOnce.R.m)                                // n * 10, filled at the entry
        assertEquals(40, M2EnumOnce.G.m)
        assertEquals(2, M2EnumCounter.calls)                            // 2 entries x once (was 2 x twice)

        assertEquals(4, M2EnumBodyOnce.X.n)
        assertEquals(40, M2EnumBodyOnce.X.m)                            // filled at the per-entry body's base call
        assertEquals("x", M2EnumBodyOnce.X.tag())
        assertEquals(40, M2EnumBodyOnce.Y.m)
        assertEquals(2, M2EnumBodyCounter.calls)                        // 2 entries x once
    }

    // A default's `this` read binds per RECEIVER KIND. Each assertion below was a runtime NullReferenceException
    // before the kind-directed binding (the default read a dispatch-owner member off the extension receiver).
    @TestAttribute
    fun defargsReceiverKind() {
        val h = M2RecvKind(10)
        assertEquals(30, h.run())                                       // 3 * dispatch k=10
        assertEquals(30, h.runInline())                                 // the same through the inline splice
        assertEquals(21, h.runParam())                                  // 3 * 7 — the value-param arm, unchanged
        assertEquals(15, M2RecvOuter(5).R().run())                      // 3 * OUTER k=5, via the enclosing chain
        assertEquals(9, 3.m2SelfScaled())                               // 3 * extension receiver 3 — sound arm
    }
}
