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
    // `inline` WITHOUT a function-typed argument does NOT splice (the gate is `isInline && hasLambdaArg`), so this
    // takes the same ordinary filledArgs path as `scaledV`.
    inline fun Int.scaledI(f: Int = k): Int = this * f
    // The real InlineSplice arm: the default reads dispatch while the body also has an extension receiver.
    inline fun Int.scaledCarrier(f: Int = k, body: (Int) -> Int): Int = body(this * f)
    fun Int.viaParam(base: Int, f: Int = base): Int = this * f
    fun run(): Int = 3.scaledV()
    fun runInline(): Int = 3.scaledI()
    fun runCarrier(): Int = 3.scaledCarrier { it }
    fun runParam(): Int = 3.viaParam(7)
}

// The same collapse reached the ENCLOSING-this chain: an inner class's member EXTENSION whose default reads the
// OUTER class's `this@Outer` had that chain hang off the extension receiver instead of its own dispatch receiver.
class M2RecvOuter(val k: Int) {
    inner class R {
        fun Int.viaOuter(f: Int = k): Int = this * f
        inline fun Int.viaOuterCarrier(f: Int = k, body: (Int) -> Int): Int = body(this * f)
        fun run(): Int = 3.viaOuter()
        fun runCarrier(): Int = 3.viaOuterCarrier { it }
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

// The two call sites that are not expressions — a constructor DELEGATION and an ENUM ENTRY. A delegation's arguments
// ride the constructor DECLARATION, so its evaluation plan lowers to `preStmts` emitted ahead of the delegating call;
// an enum entry's `NAME(args)` is an ordinary expression (a static field initializer). Either way a value a filled
// default reads runs exactly once. The counter is a per-test instance except for the enum, whose entries are
// initialized ONCE per process by the static initializer — so those two read a companion counter.
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
// ORDER at a delegation, the shape a bare single-evaluation count cannot see: Kotlin evaluates the value the
// `: this(…)` / `: super(…)` SUPPLIES before any of the target's defaults. A delegation's arguments ride the
// constructor DECLARATION, so that order is carried by the plan's `preStmts` — emitted ahead of the delegating call —
// rather than by a wrapping expression.
object M2DelOrderLog {
    var s = ""
    fun p(): Int { s += "p"; return 2 }
    fun d(): Int { s += "d"; return 3 }
}
class M2DelOrder(val x: Int, val a: Int = M2DelOrderLog.d(), val b: Int = a * 10) {
    constructor(unused: String) : this(M2DelOrderLog.p())
}
open class M2DelOrderBase(val x: Int, val a: Int = M2DelOrderLog.d(), val b: Int = a * 10)
class M2DelOrderSub : M2DelOrderBase(M2DelOrderLog.p())

// A fill whose SLOT precedes a slot the call SUPPLIES. Kotlin evaluates every supplied value before ANY of the
// callee's defaults, whatever slots they sit in — so the positional argument array is not an evaluation plan here,
// and the call needs one even though nothing reads a second time. `M2SlotOrderChain` is the sibling where a later
// default also reads the fill: it was already correct, because sharing forced a binding that pinned the order, which
// is exactly the accident that hid this one.
object M2SlotOrderLog {
    var s = ""
    fun d(): Int { s += "d"; return 3 }
    fun p(): Int { s += "p"; return 7 }
}
fun m2SlotOrder(a: Int = M2SlotOrderLog.d(), c: Int): Int = a * 1000 + c
fun m2SlotOrderChain(a: Int = M2SlotOrderLog.d(), b: Int = a * 10, c: Int): Int = a * 1000 + b + c
class M2SlotOrderCtor(val a: Int = M2SlotOrderLog.d(), val c: Int)

// A GENERIC base whose constructor defaults chain, delegated from a class with a DIFFERENT type-parameter frame. The
// bound default's declared type is written in `M2GenBase`'s frame (`T`), which names a different slot in
// `M2GenDerived`'s (`X`, `Y`) — a local declared with it is either wrongly typed or unloadable.
open class M2GenBase<T>(val a: T, val b: T = a, val c: T = b)
class M2GenDerived<X, Y>(y: Y) : M2GenBase<Y>(y) { fun probe(): String = "$a/$b/$c" }

// A default is the CALLEE's expression evaluated in the CALLER's frame, so EVERY type it mentions has to be closed
// against this call site's instantiation — not just the omitted parameter's own type. A positional type variable is a
// slot in the callee's frame, and the caller's frame either has a different slot there or none at all. The battery
// walks what a default is allowed to read: the RECEIVER's property, a member CALL on the receiver, the receiver inside
// a generic CONSTRUCTOR's default, a receiver read chained into a later default, an EXTENSION receiver, and the
// callee's OWN type parameter standing beside the owner's. The last two are controls that were already correct.
//   NOT here, and not a type-frame question: a GENERIC inner class reading its outer instance
// (`class O<T>(val v: T) { inner class In(val x: T = v) }`) NullReferenceExceptions — its `__outer` capture is null,
// identically at `3fedd238` and with no default argument involved. Its non-generic twin is covered by
// `defargsEnclosingReadAtAMemberExtension`.
class M2FrameOwnerProp<T>(val v: T) { fun one(a: T = v): String = "$a" }
class M2FrameOwnerCall<T>(val v: T) { fun tag(): String = "t$v"; fun one(a: String = tag()): String = a }
class M2FrameOwnerCtor<T>(val v: T, val w: T = v, val x: T = w)
class M2FrameOwnerChain<T>(val v: T) { fun pair(a: T = v, b: T = a): String = "$a$b" }
fun <T> T.m2FrameExt(a: T = this): String = "$a"
class M2FrameOwnAndOwner<T>(val v: T) { fun <U> two(u: U, a: T = v): String = "$u$a" }
class M2FrameControls<T>(val v: T) {
    fun konst(a: Int = 5): String = "$v$a"
    fun prior(q: Int, a: Int = q * 2): String = "$v$q$a"
}
// ...and the same rule at any NESTING DEPTH. A default may itself be a call that fills a default of its own, and each
// frame closes against the one it is spliced into, not against the call site directly — so the substitutions have to
// COMPOSE. Closing `M2NestB.X` against `M2NestC.T` and stopping leaves `M2NestC.T` open in a caller that has no such
// slot, which is the same InvalidProgramException one level out.
// A default whose EXPRESSION reads nothing of the call, but whose TYPES are the callee's. The closure has to apply to
// every default rendered into another frame, not only the ones that splice a value — a type-only default left the
// callee's positional type variables naming slots the caller's frame does not have.
class M2TypeOnly<T>(val v: T) {
    fun ownerTv(xs: List<T> = emptyList()): Int = xs.size                      // the OWNER's type parameter
    fun <U> funTv(xs: List<U> = emptyList()): Int = xs.size                    // the CALLEE's own
    fun <U> mixed(xs: List<Pair<T, U>> = emptyList()): Int = xs.size           // both
    fun nested(xs: List<T> = emptyList(), n: Int = xs.size + 1): Int = n       // a later default reading the first
}
// ...and at the two call shapes that ride a DECLARATION rather than an expression.
open class M2TypeOnlyBase<T>(val xs: List<T> = emptyList()) { val n: Int get() = xs.size }
class M2TypeOnlySub<A, B> : M2TypeOnlyBase<B>()
enum class M2TypeOnlyEnum(val xs: List<String> = emptyList()) { ONE, TWO(listOf("a")) }

class M2NestB<X>(val x: X) { fun get(a: X = x): X = a }
class M2NestC<T>(val b: M2NestB<T>) { fun one(a: T = b.get()): T = a }
class M2NestD<U>(val c: M2NestC<U>) { fun two(a: U = c.one()): U = a }
class M2NestE<V>(val d: M2NestD<V>) { fun three(a: V = d.two()): V = a }

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

// A CROSS-MODULE data-class `copy` that omits a field. The frontend artifact preserves no default VALUE for a
// referenced callee, so kotc RECONSTRUCTS each omitted field as a read of the call's receiver; that read must take the
// receiver's single-evaluation temp. Reconstructing it as a second RENDERING of the receiver expression ran the
// receiver once per omitted field ON TOP of the call's own use of it. `kotlin.Pair`/`kotlin.Triple` come from the
// stdlib, so every `copy` below is cross-module by construction.
class M2CopyLog {
    var calls = 0
    var s = ""
    fun pair(): Pair<Int, Int> { calls++; s += "P"; return 1 to 2 }
    fun triple(): Triple<Int, Int, Int> { calls++; s += "T"; return Triple(1, 2, 3) }
    fun arg(): Int { s += "a"; return 9 }
}
// A receiver expression carrying a LAMBDA: a second rendering would lift a second copy of the lambda as well as re-run
// the call that takes it.
object M2CopyLambdaCounter { var calls = 0 }
fun m2CopyVia(f: () -> Int): Pair<Int, Int> { M2CopyLambdaCounter.calls++; return f() to 2 }

// A data class may ALSO declare a differently-signed `copy` OVERLOAD of its own — only the generated signature is
// reserved. Its defaults are ordinary expressions, not `this.<field>` reads, so the synthetic-copy test must not claim
// it (`copy` + `isData` alone did). A CONTROL, not a regression test — it passes with the selector reverted, and the
// tightened selector is UNMEASURED: reaching the mis-selection needs a data class with a `copy` overload in a
// REFERENCED module, and the only cross-module source that preserves the `data` nature is the frontend KLIB, i.e. the
// stdlib, which declares no such class. What IS verified here is that the overload compiles, resolves and returns its
// own ordinary default.
data class M2CopyOver(val x: Int, val y: Int) {
    fun copy(tag: String, z: Int = x * 2): String = "$tag/$z/$y"
}

// A FILLED default that a later default reads is bound to a temp declared AHEAD of the call node. Kotlin evaluates the
// receiver, then each argument, and only then the callee's defaults — so binding it must not move it in front of them.
// The side-effecting default is a TOP-LEVEL call on purpose: a default calling a MEMBER of the callee's own class reads
// the dispatch receiver, which the pre-pass already bound for that reason, and would not exercise the ordering rule.
object M2FillLog {
    var s = ""
    fun mk(): Int { s += "d"; return 3 }
    fun arg(): Int { s += "p"; return 7 }
}
fun m2FillMk(): Int = M2FillLog.mk()
class M2FillOrder {
    fun f(a: Int = m2FillMk(), b: Int = a * 10): Int = a * 1000 + b
    fun g(p: Int, a: Int = m2FillMk(), b: Int = a * 10): Int = p + a * 1000 + b
    // A supplied argument whose parameter index is HIGHER than the bound fill's: the fill's temp must still sort after
    // it, which a bare parameter-index key does not express.
    fun h(a: Int = m2FillMk(), b: Int = a * 10, c: Int): Int = c + a * 1000 + b
}
fun m2FillHost(): M2FillOrder { M2FillLog.s += "H"; return M2FillOrder() }
class M2FillCtorHost(val p: Int, val a: Int = m2FillMk(), val b: Int = a * 10)

// An EXTENSION call site fills its omitted defaults through the same pass as every other call, and must run that pass
// ONCE. Filling is not a pure rendering — it binds a filled default that a LATER default reads to a temp — so a second
// run declared a SECOND temp holding the same default expression, and both initializers ran.
class M2ExtSource { var calls = 0; fun bump(): Int { calls++; return 3 } }
fun M2ExtSource.m2ExtChain(a: Int = bump(), b: Int = a * 10): Int = a * 1000 + b
class M2ExtOwner(val k: Int) {
    fun M2ExtSource.memChain(a: Int = bump(), b: Int = a * 10): Int = a * 1000 + b + k
    fun run(s: M2ExtSource): Int = s.memChain()
}

// A member extension inside an INNER class whose filled default reads an ENCLOSING instance — the shape where a call
// has THREE live values (the enclosing chain, the dispatch receiver and the extension receiver) and the default reads
// the one the call site never writes. Every side effect is logged so the count AND the order are asserted.
object M2EncLog { var s = "" }
class M2EncSrc(val n: Int) { fun bump(): Int { M2EncLog.s += "s"; return n } }
fun m2EncSrc(): M2EncSrc { M2EncLog.s += "S"; return M2EncSrc(2) }
class M2EncOuter(val k: Int) {
    fun mark(): Int { M2EncLog.s += "K"; return k }
    inner class M2EncInner {
        // `mark()` is a member of the ENCLOSING class, so this default reads `this@M2EncOuter` — reached from the
        // member extension's DISPATCH receiver, never from its extension receiver.
        fun M2EncSrc.encOnly(a: Int = mark(), b: Int = a * 10): Int = a * 1000 + b
        // …and one reading BOTH the enclosing instance and the extension receiver.
        fun M2EncSrc.encAndExt(a: Int = mark() + bump(), b: Int = a * 10): Int = a * 1000 + b
        fun goEncOnly(): Int = m2EncSrc().encOnly()
        fun goEncAndExt(): Int = m2EncSrc().encAndExt()
    }
    fun inner(): M2EncInner { M2EncLog.s += "I"; return M2EncInner() }
}

// §7a — the QUALIFIER of an `object`/companion call, at a call site that needs an evaluation plan.
//
// A plain `companion object` is flattened onto its enclosing class, so `M2Flat.make(…)` emits a receiver-less static
// call: the qualifier is not a value the emitted shape can hold, and the plan must not mint one for it (a binding
// nothing reads, holding a read of an `INSTANCE` field this representation never emits, is not an evaluation that
// was skipped — there is nothing there to evaluate). A real `object` keeps its singleton and its `INSTANCE` read,
// and a default that reads the object's own member makes the qualifier a second reader of it.
//
// These assert VALUES, not initialization ORDER: when a companion's initialization runs is the CLR type
// initializer's schedule (§7a), and this backend has separate, older gaps there — a flattened companion's `init { }`
// block is not emitted at all — which are not what this battery is about and must not be pinned here by accident.
class M2Flat {
    companion object {
        // A default reading an EARLIER parameter forces a plan at every call site, supplied or not.
        fun make(width: Int = 2, height: Int = width * 3): Int = width * 100 + height
    }
}

// …and the ORDER half of the same rule. A REAL object's `INSTANCE` load runs the object's own body, so it is an
// observable evaluation and Kotlin runs it BEFORE every argument. `M2Ord.take(m2Mark("A"))` must therefore log
// "O" then "A" — the qualifier needs a plan binding to hold that position, because the default reading an earlier
// parameter forces the argument into a pre-call local that would otherwise overtake it.
val m2OrdLog = StringBuilder()
fun m2Mark(tag: String): Int { m2OrdLog.append(tag); return tag.length }

object M2Ord {
    init { m2OrdLog.append("O") }
    fun take(a: Int, b: Int = a * 2): Int = a * 10 + b
}

object M2Solo {
    val k = 4
    // Reads the object's own member, so the default binds the call's DISPATCH RECEIVER — the qualifier now has two
    // readers (the call's own receiver slot and this default) and is read in place at both.
    fun scale(a: Int = k, b: Int = a * k): Int = a + b
    // An inline member: the third receiver-binding site, where the qualifier reaches the payload rather than a slot.
    inline fun twice(n: Int, f: (Int) -> Int): Int = f(n) + f(n)
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

    // Single evaluation of the receiver a CROSS-MODULE data-class `copy` reconstructs its omitted fields from. Each
    // `calls`/`s` assertion is the load-bearing one — the copied VALUES were already right, the receiver just ran
    // repeatedly. The pre-fix counts are noted per case.
    @TestAttribute
    fun defargsCrossModuleCopySingleEval() {
        val a = M2CopyLog()
        assertEquals("(1, 9)", a.pair().copy(second = 9).toString())     // (1, 9)
        assertEquals(1, a.calls)                                        // 1  was 2 (one omitted field + the call)

        val b = M2CopyLog()
        assertEquals("(1, 9, 3)", b.triple().copy(second = 9).toString())
        assertEquals(1, b.calls)                                        // 1  was 3 (two omitted fields + the call)

        val c = M2CopyLog()
        assertEquals("(1, 2, 3)", c.triple().copy().toString())          // every field omitted
        assertEquals(1, c.calls)                                        // 1  was 4

        // ORDER: Kotlin evaluates the receiver, then the argument. Re-rendering the receiver per omitted field also put
        // a receiver evaluation AFTER the argument (the log read "TTaT").
        val d = M2CopyLog()
        assertEquals("(1, 9, 3)", d.triple().copy(second = d.arg()).toString())
        assertEquals("Ta", d.s)                                         // Ta  receiver first, then the argument
        assertEquals(1, d.calls)

        // A receiver expression carrying a lambda: re-rendering it lifted a second copy of the lambda too.
        M2CopyLambdaCounter.calls = 0
        assertEquals("(7, 5)", m2CopyVia { 7 }.copy(second = 5).toString())
        assertEquals(1, M2CopyLambdaCounter.calls)                       // 1  was 2

        // A data class's own `copy` OVERLOAD is a different function with ordinary defaults — the synthetic-copy test
        // must not claim it (name + `isData` alone did; the signature has to match the generated one).
        assertEquals("t/2/2", M2CopyOver(1, 2).copy("t"))                // t/2/2  z = x * 2, an ordinary default
        assertEquals("M2CopyOver(x=1, y=9)", M2CopyOver(1, 2).copy(y = 9).toString())
    }

    // A FILLED default bound for single evaluation must not move AHEAD of the values the call SUPPLIES: Kotlin
    // evaluates the receiver, then each argument, and only then the callee's defaults.
    @TestAttribute
    fun defargsFilledDefaultOrder() {
        M2FillLog.s = ""
        assertEquals(3030, m2FillHost().f())                            // a = mk() = 3, b = a * 10 = 30
        assertEquals("Hd", M2FillLog.s)                                 // Hd   receiver first (was "dH")

        M2FillLog.s = ""
        assertEquals(3037, m2FillHost().g(M2FillLog.arg()))             // 7 + 3000 + 30
        assertEquals("Hpd", M2FillLog.s)                                // Hpd  receiver, argument, then the default (was "dHp")

        // The supplied argument sits at a HIGHER parameter index than the bound fill.
        M2FillLog.s = ""
        assertEquals(3037, m2FillHost().h(c = M2FillLog.arg()))
        assertEquals("Hpd", M2FillLog.s)                                // Hpd  (was "Hdp" — the fill ran before the argument)

        M2FillLog.s = ""
        val c = M2FillCtorHost(M2FillLog.arg())
        assertEquals(3, c.a)
        assertEquals(30, c.b)
        assertEquals("pd", M2FillLog.s)                                 // pd   argument before the default (was "dp")
    }

    // The same single-evaluation rule at an EXTENSION call site, whose default-filling pass used to run twice.
    @TestAttribute
    fun defargsSingleEvalExtensionCall() {
        val a = M2ExtSource()
        assertEquals(3030, a.m2ExtChain())                              // a = bump() = 3, b = a * 10 = 30
        assertEquals(1, a.calls)                                        // 1  was 2 (the fill ran once per rendering)

        val b = M2ExtSource()
        assertEquals(3031, M2ExtOwner(1).run(b))                        // a MEMBER extension: both receivers live
        assertEquals(1, b.calls)                                        // 1  was 2
    }

    // A member extension in an INNER class whose filled default reads an ENCLOSING instance: the call has THREE live
    // values (the enclosing chain, the dispatch receiver and the extension receiver), and the default reads the one the
    // call site never writes. The enclosing read is reached from the DISPATCH receiver, and must be evaluated once and
    // after the values the call supplies. Both halves come from the plan: the enclosing instance is the dispatch
    // receiver's binding (so one evaluation, however many defaults read it), and the fill is a default-phase binding,
    // which the order rule keeps behind every supplied value.
    @TestAttribute
    fun defargsEnclosingReadAtAMemberExtension() {
        M2EncLog.s = ""
        assertEquals(7070, M2EncOuter(7).inner().goEncOnly())           // a = mark() = 7, b = 70
        assertEquals("ISK", M2EncLog.s)                                 // ISK  inner, extension receiver, then the default (was "ISKK")

        M2EncLog.s = ""
        assertEquals(9090, M2EncOuter(7).inner().goEncAndExt())         // a = mark() + bump() = 7 + 2 = 9, b = 90
        assertEquals("ISKs", M2EncLog.s)                                // ISKs (was "ISKsKs")
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

        // ORDER at those same two call sites: the SUPPLIED value first, then the filled default.
        M2DelOrderLog.s = ""
        val o = M2DelOrder("")
        assertEquals(2, o.x); assertEquals(3, o.a); assertEquals(30, o.b)
        assertEquals("pd", M2DelOrderLog.s)                             // pd   `: this(p())` before the target's default

        M2DelOrderLog.s = ""
        val ob = M2DelOrderSub()
        assertEquals(2, ob.x); assertEquals(3, ob.a); assertEquals(30, ob.b)
        assertEquals("pd", M2DelOrderLog.s)                             // pd   `: super(p())` before the base's default
    }

    // A fill sitting in a slot BEFORE the slot the call supplies: the emitted argument array's order is NOT Kotlin's,
    // so the call needs an evaluation plan even though no value is read twice.
    @TestAttribute
    fun defargsFillBeforeASuppliedSlot() {
        M2SlotOrderLog.s = ""
        assertEquals(3007, m2SlotOrder(c = M2SlotOrderLog.p()))         // a = 3, c = 7
        assertEquals("pd", M2SlotOrderLog.s)                            // pd   the argument, then the default (was "dp")

        // The sibling where a later default also reads the fill — correct before, and it must stay correct.
        M2SlotOrderLog.s = ""
        assertEquals(3037, m2SlotOrderChain(c = M2SlotOrderLog.p()))    // a = 3, b = 30, c = 7
        assertEquals("pd", M2SlotOrderLog.s)                            // pd

        M2SlotOrderLog.s = ""
        val k = M2SlotOrderCtor(c = M2SlotOrderLog.p())
        assertEquals(3, k.a); assertEquals(7, k.c)
        assertEquals("pd", M2SlotOrderLog.s)                            // pd   a constructor is the same call site
    }

    // A generic base's chained constructor defaults, bound in a derived class whose type-parameter frame differs. The
    // bound local must be typed in the frame it LIVES in; the base's `T` names a different slot there.
    @TestAttribute
    fun defargsGenericBaseDelegationChain() {
        assertEquals("7/7/7", M2GenDerived<String, Int>(7).probe())     // 7/7/7  (was InvalidProgramException)
        assertEquals("k/k/k", M2GenDerived<Int, String>("k").probe())   // k/k/k
    }

    // Splicing a default into a caller closes EVERY open type variable it mentions, across everything a default may
    // read. Each of the first four was an InvalidProgramException at load; the last two are controls.
    @TestAttribute
    fun defargsCloseCalleeTypeFrame() {
        assertEquals("7", M2FrameOwnerProp(7).one())                    // the receiver's property
        assertEquals("s", M2FrameOwnerProp("s").one())                  // ...at a second instantiation
        assertEquals("t7", M2FrameOwnerCall(7).one())                   // a member CALL on the receiver
        val c = M2FrameOwnerCtor(7)
        assertEquals(7, c.w); assertEquals(7, c.x)                      // the receiver inside a generic ctor's default
        assertEquals("77", M2FrameOwnerChain(7).pair())                 // ...chained into a later default
        assertEquals("7", 7.m2FrameExt())                               // an EXTENSION receiver
        assertEquals("k", "k".m2FrameExt())
        assertEquals("u7", M2FrameOwnAndOwner(7).two("u"))              // the callee's own type param beside the owner's
        assertEquals("75", M2FrameControls(7).konst())                  // CONTROL: a const default
        assertEquals("736", M2FrameControls(7).prior(3))                // CONTROL: a prior-param default

        // ...at nesting depth 1, 2 and 3: a default filling a default filling a default. Each frame closes against
        // the one it is spliced into, so the substitutions compose all the way out to the call site.
        assertEquals(7, M2NestC(M2NestB(7)).one())                      // depth 1
        assertEquals("s", M2NestC(M2NestB("s")).one())
        assertEquals(7, M2NestD(M2NestC(M2NestB(7))).two())             // depth 2
        assertEquals(7, M2NestE(M2NestD(M2NestC(M2NestB(7)))).three())  // depth 3
        assertEquals("z", M2NestE(M2NestD(M2NestC(M2NestB("z")))).three())

        // ...and where the default's EXPRESSION reads nothing at all and only its TYPES are the callee's.
        assertEquals(0, M2TypeOnly(7).ownerTv())                        // the owner's type parameter
        assertEquals(0, M2TypeOnly(7).funTv<String>())                  // the callee's own
        assertEquals(0, M2TypeOnly(7).mixed<String>())                  // both
        assertEquals(1, M2TypeOnly(7).nested())                         // a later default reading the first
        assertEquals(0, M2TypeOnlySub<Int, String>().n)                 // a `: super(…)` delegation
        assertEquals(0, M2TypeOnlyEnum.ONE.xs.size)                     // an enum entry
        assertEquals(1, M2TypeOnlyEnum.TWO.xs.size)
    }

    // A default's `this` read binds per RECEIVER KIND. Each assertion is tagged with what it was before the
    // kind-directed binding: WAS-NRE threw a NullReferenceException (the default read a dispatch-owner member off
    // the extension receiver's VALUE), CONTROL passed already and must keep passing.
    @TestAttribute
    fun defargsReceiverKind() {
        val h = M2RecvKind(10)
        assertEquals(30, h.run())                                       // WAS-NRE  3 * dispatch k=10
        assertEquals(30, h.runInline())                                 // WAS-NRE  lambda-less `inline`: ordinary path
        assertEquals(30, h.runCarrier())                                // carrier: dispatch and extension stay distinct
        assertEquals(21, h.runParam())                                  // CONTROL  3 * 7 — the value-param arm
        assertEquals(15, M2RecvOuter(5).R().run())                      // WAS-NRE  3 * OUTER k=5, enclosing chain
        assertEquals(15, M2RecvOuter(5).R().runCarrier())               // carrier: outer chain roots at dispatch
        assertEquals(9, 3.m2SelfScaled())                               // CONTROL  3 * extension receiver 3
    }

    /** §7a — an `object`/companion qualifier at a call site that carries an evaluation plan. */
    @TestAttribute
    fun defargsObjectQualifierIsNotAPlanValue() {
        // FLATTENED companion: the emitted static call has no receiver slot at all.
        assertEquals(206, M2Flat.make())                                // width=2, height=2*3
        assertEquals(515, M2Flat.make(5))                               // height still filled from the supplied width
        assertEquals(202, M2Flat.make(height = 2))                      // named-middle omission: `width` fills to 2

        // REAL singleton: the qualifier IS a value, read by the call's receiver slot AND by the default that reads
        // the object's own member. Reading it twice is free — it is the same singleton either way.
        assertEquals(20, M2Solo.scale())                                // a=k=4, b=4*4
        assertEquals(15, M2Solo.scale(3))                               // a=3, b=3*4
        assertEquals(10, M2Solo.scale(b = 6))                           // named-middle omission: a fills to k=4
        assertEquals(30, M2Solo.twice(3) { it * 5 })                    // the inline site, qualifier in the payload
    }

    /** §7a — a REAL object's qualifier is an observable evaluation, and Kotlin runs it before the arguments. */
    @TestAttribute
    fun defargsRealObjectQualifierIsEvaluatedBeforeTheArguments() {
        m2OrdLog.setLength(0)
        assertEquals(12, M2Ord.take(m2Mark("A")))       // a=1 ("A".length), b=2
        // "AO" would mean the object was initialized only when the call finally touched it — after the argument's
        // side effect, and after an initializer that throws would have had to run.
        assertEquals("OA", m2OrdLog.toString())
    }
}
