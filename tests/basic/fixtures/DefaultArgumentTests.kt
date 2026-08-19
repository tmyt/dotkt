// Default-argument battery (feature fixture) — migrates the default-argument family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals is strictly stronger (typed) than the old text diff; every asserted value is preserved 1:1
// (see the `// <expected>` comments). Top-level declarations are `DefaultArg`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-defaultArguments  -> defaultArg_defargs   C3: cross+same-module default args; an omitted middle default must not shift a
//                                later provided arg's slot (joinToString / substringAfter `= this` / copy(field=))
//   il-defaultArguments2 -> defaultArg_defargs2  same-module default referencing ANOTHER value param (`b = a*10`, `c = a+b`)
//
// The data class was `P`; renamed `DefaultArgP` for collision-freedom, so its toString reads `DefaultArgP(...)` — the class name is
// incidental to the subject (default-arg slot correctness), which is unchanged.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

data class DefaultArgP(val x: Int, val y: Int, val z: Int)

fun defaultArgGreet(name: String, greeting: String = "Hello", punct: String = "!"): String = "$greeting, $name$punct"

fun defaultArgF(a: Int, b: Int = a * 10): Int = a + b
fun defaultArgG(x: Int, y: Int = x + 1): Int = x * y
fun defaultArgH(a: Int, b: Int = 3, c: Int = a + b): Int = a * 100 + b * 10 + c

// #235: the CONSTRUCTOR shapes of the same defaults — a default that reads an EARLIER constructor parameter, in a
// plain class, a data class, reading TWO earlier params, with a later arg passed by name, and on a secondary ctor.
// The INNER class and the LOCAL class (defaultArgLocalDefault) additionally cover the arg positions a `new` PREPENDS in front
// of the filled args — the enclosing instance and the lifted captures — which must not shift the filled slots.
class DefaultArgRect(val w: Int, val h: Int = w * 2)
data class DefaultArgDRect(val w: Int, val h: Int = w * 3, val tag: String = "d")
class DefaultArgTri(val a: Int, val b: Int = a + 1, val c: Int = a * 10 + b)
class DefaultArgSec(val v: Int) {
    constructor(a: Int, b: Int, c: Int = a * 5) : this(a + b + c)
}
class DefaultArgOwn(val n: Int) {
    inner class DefaultArgIn(val a: Int, val b: Int = a * 2) {
        val all: Int get() = n * 100 + a * 10 + b
    }
}

fun defaultArgLocalDefault(seed: Int): Int {
    class L(val p: Int, val q: Int = p + seed)
    return L(2).q
}

// #235: the constructor call sites that are NOT a `new` — a `: this(…)` / `: super(…)` delegation and an enum
// entry's `NAME(args)` (both the plain-entry form and a per-entry BODY's base call). Each is an omitting call site,
// so an omitted default must be filled there too, not dropped (dropping it slides every later arg one slot down).
class DefaultArgDel(val a: Int, val b: Int = a * 2) {
    constructor() : this(3)
}
open class DefaultArgDelBase(val a: Int, val b: Int = a + 1)
class DefaultArgDelSub : DefaultArgDelBase(2)

enum class DefaultArgHue(val rgb: Int, val label: String = "c") {
    R(1),
    G(2, "g")
}

enum class DefaultArgOp(val arity: Int, val label: String = "op") {
    ADD(2) { override fun apply(a: Int, b: Int): Int = a + b },
    NEG(1, "neg") { override fun apply(a: Int, b: Int): Int = -a };

    abstract fun apply(a: Int, b: Int): Int
}

// #235: a constructor default that reads the ENCLOSING instance — one level up (the call's own dispatch receiver)
// and two (through that level's `__outer`).
class DefaultArgEncl(val k: Int) {
    inner class DefaultArgEIn(val x: Int = k * 4)
}
class DefaultArgDeep(val q: Int) {
    inner class DefaultArgDMid {
        inner class DefaultArgDIn(val x: Int = q + 1)
    }
}

// #235: a closure captures the callee's OWN parameter and a recursive call inside it omits a default that reads
// that same parameter — filling must not clobber the closure's capture binding for it.
fun defaultArgRec(a: Int, b: Int = a * 2): Int {
    if (a <= 0) return b
    val f = { a + defaultArgRec(a - 1) }
    return f()
}

// #235: the filled expression is bound BY SYMBOL, so an argument that itself reads `this` (`c.m(k)` passes `this.k`)
// keeps its own receiver instead of being re-pointed at the call's receiver. DefaultArgSelfArg.k and DefaultArgSelfHolder.k differ so
// a wrong receiver is a wrong VALUE, not just a wrong type.
class DefaultArgSelfArg(val k: Int) {
    fun m(a: Int, b: Int = a * 10): Int = a * 1000 + b
}
class DefaultArgSelfHolder {
    val k = 5
    val c = DefaultArgSelfArg(7)
    fun call(): Int = c.m(k)
}

// #235: the enclosing instance reached through a NON-`this` receiver (`o.DefaultArgEIn()` inside another class), and an inner
// class's own secondary constructor delegating with the target's default reading the enclosing instance.
class DefaultArgEnclHolder(val o: DefaultArgEncl) {
    fun x(): Int = o.DefaultArgEIn().x
}
class DefaultArgEnclDel(val k: Int) {
    inner class In(val a: Int, val b: Int = k) {
        constructor() : this(1)
    }
}

// #235: a MEMBER function (not a constructor) of an inner class, whose default reads the enclosing instance.
class DefaultArgEnclMember(val k: Int) {
    inner class Q {
        fun f(x: Int = k * 4): Int = x
    }
}

// A callee with TWO receivers: a default's `this` read binds to the receiver of ITS OWN KIND. A member EXTENSION
// whose default reads the DISPATCH receiver (`this@Owner.k`) had every receiver param bound to one collapsed
// expression — the EXTENSION receiver — so the default read an Owner member off the extension receiver's VALUE and
// reached CIL as `DefaultArgRecvKind.get_k()` on an Int: a NullReferenceException at runtime with nothing loud at compile
// time. The values differ so a wrong receiver is a wrong VALUE, not merely a wrong type.
class DefaultArgRecvKind(val k: Int) {
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
class DefaultArgRecvOuter(val k: Int) {
    inner class R {
        fun Int.viaOuter(f: Int = k): Int = this * f
        inline fun Int.viaOuterCarrier(f: Int = k, body: (Int) -> Int): Int = body(this * f)
        fun run(): Int = 3.viaOuter()
        fun runCarrier(): Int = 3.viaOuterCarrier { it }
    }
}

// A TOP-LEVEL extension whose default reads the extension receiver — the sound single-receiver arm, pinned so the
// kind-directed binding cannot regress it.
fun Int.defaultArgSelfScaled(f: Int = this): Int = this * f

// #235: a lifted LOCAL class whose secondary constructor delegates — the capture params are leading args of the
// TARGET constructor too, and the delegation's own omitted default reads the capture.
fun defaultArgLocalDelegate(seed: Int): Int {
    class L(val p: Int, val q: Int = seed) {
        constructor() : this(1)
    }
    return L().q
}

// #235: SINGLE EVALUATION of a value a filled default splices. Every counter lives on a per-test instance (no shared
// top-level state) so the suite can run these in parallel.
fun defaultArgSideF(a: Int, b: Int = a * 10): Int = a + b
class DefaultArgSideC(val a: Int, val b: Int = a * 10) { val sum: Int get() = a + b }
class DefaultArgSideHolder(val s: String) {
    fun tag(t: String = s): String = s + "/" + t
}
class DefaultArgEvalOnce {
    var calls = 0
    fun next(): Int { calls++; return 4 }
    fun holder(): DefaultArgSideHolder { calls++; return DefaultArgSideHolder("h") }
    fun encl(): DefaultArgEncl { calls++; return DefaultArgEncl(9) }
}

// #235: an OMITTED default is a value too. If a later omitted default reads it, the first default must be evaluated
// once and shared, just like an explicitly provided side-effecting argument.
class DefaultArgDefaultSource {
    var calls = 0
    fun bump(): Int { calls++; return 3 }
}
fun defaultArgDefaultChainF(source: DefaultArgDefaultSource, a: Int = source.bump(), b: Int = a * 10): Int = a * 1000 + b
class DefaultArgDefaultChainC(val source: DefaultArgDefaultSource, val a: Int = source.bump(), val b: Int = a * 10)
class DefaultArgDefaultChainDel(val source: DefaultArgDefaultSource, val a: Int = source.bump(), val b: Int = a * 10) {
    constructor(source: DefaultArgDefaultSource, unused: String) : this(source)
}
object DefaultArgDefaultEnumSource {
    var calls = 0
    fun bump(): Int { calls++; return 3 }
}
enum class DefaultArgDefaultChainE(val a: Int = DefaultArgDefaultEnumSource.bump(), val b: Int = a * 10) {
    ONLY
}

// The two call sites that are not expressions — a constructor DELEGATION and an ENUM ENTRY. A delegation's arguments
// ride the constructor DECLARATION, so its evaluation plan lowers to `preStmts` emitted ahead of the delegating call;
// an enum entry's `NAME(args)` is an ordinary expression (a static field initializer). Either way a value a filled
// default reads runs exactly once. The counter is a per-test instance except for the enum, whose entries are
// initialized ONCE per process by the static initializer — so those two read a companion counter.
class DefaultArgDelOnce(val p: Int, val q: Int = p * 10) {
    constructor(unused: String) : this(DefaultArgDelCounter.next())
}
open class DefaultArgDelBaseOnce(val p: Int, val q: Int = p * 10)
class DefaultArgDelSubOnce : DefaultArgDelBaseOnce(DefaultArgDelCounter.next())
object DefaultArgDelCounter {
    var calls = 0
    fun next(): Int { calls++; return 4 }
}
// A mixed enum covers both declaration-shaped call sites in one type: R initializes the direct base entry field,
// while X forwards its baked-in argument through the per-entry subclass's base call.
enum class DefaultArgEnumOnce(val n: Int, val m: Int = n * 10) {
    R(DefaultArgEnumCounter.next()),
    X(DefaultArgEnumCounter.next()) { override fun tag(): String = "x" };

    open fun tag(): String = "r"
}
object DefaultArgEnumCounter { var calls = 0; fun next(): Int { calls++; return 4 } }
// ORDER at a delegation, the shape a bare single-evaluation count cannot see: Kotlin evaluates the value the
// `: this(…)` / `: super(…)` SUPPLIES before any of the target's defaults. A delegation's arguments ride the
// constructor DECLARATION, so that order is carried by the plan's `preStmts` — emitted ahead of the delegating call —
// rather than by a wrapping expression.
object DefaultArgDelOrderLog {
    var s = ""
    fun p(): Int { s += "p"; return 2 }
    fun d(): Int { s += "d"; return 3 }
}
class DefaultArgDelOrder(val x: Int, val a: Int = DefaultArgDelOrderLog.d(), val b: Int = a * 10) {
    constructor(unused: String) : this(DefaultArgDelOrderLog.p())
}
open class DefaultArgDelOrderBase(val x: Int, val a: Int = DefaultArgDelOrderLog.d(), val b: Int = a * 10)
class DefaultArgDelOrderSub : DefaultArgDelOrderBase(DefaultArgDelOrderLog.p())

// A fill whose SLOT precedes a slot the call SUPPLIES. Kotlin evaluates every supplied value before ANY of the
// callee's defaults, whatever slots they sit in — so the positional argument array is not an evaluation plan here,
// and the call needs one even though nothing reads a second time. `DefaultArgSlotOrderChain` is the sibling where a later
// default also reads the fill: it was already correct, because sharing forced a binding that pinned the order, which
// is exactly the accident that hid this one.
object DefaultArgSlotOrderLog {
    var s = ""
    fun d(): Int { s += "d"; return 3 }
    fun p(): Int { s += "p"; return 7 }
}
fun defaultArgSlotOrder(a: Int = DefaultArgSlotOrderLog.d(), c: Int): Int = a * 1000 + c
fun defaultArgSlotOrderChain(a: Int = DefaultArgSlotOrderLog.d(), b: Int = a * 10, c: Int): Int = a * 1000 + b + c
class DefaultArgSlotOrderCtor(val a: Int = DefaultArgSlotOrderLog.d(), val c: Int)

// A GENERIC base whose constructor defaults chain, delegated from a class with a DIFFERENT type-parameter frame. The
// bound default's declared type is written in `DefaultArgGenBase`'s frame (`T`), which names a different slot in
// `DefaultArgGenDerived`'s (`X`, `Y`) — a local declared with it is either wrongly typed or unloadable.
open class DefaultArgGenBase<T>(val a: T, val b: T = a, val c: T = b)
class DefaultArgGenDerived<X, Y>(y: Y) : DefaultArgGenBase<Y>(y) { fun probe(): String = "$a/$b/$c" }

// A default is the CALLEE's expression evaluated in the CALLER's frame, so EVERY type it mentions has to be closed
// against this call site's instantiation — not just the omitted parameter's own type. A positional type variable is a
// slot in the callee's frame, and the caller's frame either has a different slot there or none at all. The battery
// walks what a default is allowed to read: the RECEIVER's property, a member CALL on the receiver, the receiver inside
// a generic CONSTRUCTOR's default, a receiver read chained into a later default, an EXTENSION receiver, and the
// callee's OWN type parameter standing beside the owner's. The last two are controls that were already correct.
//   NOT here, and not a type-frame question: a GENERIC inner class reading its outer instance
// (`class O<T>(val v: T) { inner class In(val x: T = v) }`) NullReferenceExceptions — its `__outer` capture is null,
// identically at `3fedd238` and with no default argument involved. Its non-generic twin is covered by
// `defaultArgumentsEnclosingReadAtAMemberExtension`.
class DefaultArgFrameOwnerProp<T>(val v: T) { fun one(a: T = v): String = "$a" }
class DefaultArgFrameOwnerCall<T>(val v: T) { fun tag(): String = "t$v"; fun one(a: String = tag()): String = a }
class DefaultArgFrameOwnerCtor<T>(val v: T, val w: T = v, val x: T = w)
class DefaultArgFrameOwnerChain<T>(val v: T) { fun pair(a: T = v, b: T = a): String = "$a$b" }
fun <T> T.defaultArgFrameExt(a: T = this): String = "$a"
class DefaultArgFrameOwnAndOwner<T>(val v: T) { fun <U> two(u: U, a: T = v): String = "$u$a" }
class DefaultArgFrameControls<T>(val v: T) {
    fun konst(a: Int = 5): String = "$v$a"
    fun prior(q: Int, a: Int = q * 2): String = "$v$q$a"
}
// ...and the same rule at any NESTING DEPTH. A default may itself be a call that fills a default of its own, and each
// frame closes against the one it is spliced into, not against the call site directly — so the substitutions have to
// COMPOSE. Closing `DefaultArgNestB.X` against `DefaultArgNestC.T` and stopping leaves `DefaultArgNestC.T` open in a caller that has no such
// slot, which is the same InvalidProgramException one level out.
// A default whose EXPRESSION reads nothing of the call, but whose TYPES are the callee's. The closure has to apply to
// every default rendered into another frame, not only the ones that splice a value — a type-only default left the
// callee's positional type variables naming slots the caller's frame does not have.
class DefaultArgTypeOnly<T>(val v: T) {
    fun ownerTv(xs: List<T> = emptyList()): Int = xs.size                      // the OWNER's type parameter
    fun <U> funTv(xs: List<U> = emptyList()): Int = xs.size                    // the CALLEE's own
    fun <U> mixed(xs: List<Pair<T, U>> = emptyList()): Int = xs.size           // both
    fun nested(xs: List<T> = emptyList(), n: Int = xs.size + 1): Int = n       // a later default reading the first
}
// ...and at the two call shapes that ride a DECLARATION rather than an expression.
open class DefaultArgTypeOnlyBase<T>(val xs: List<T> = emptyList()) { val n: Int get() = xs.size }
class DefaultArgTypeOnlySub<A, B> : DefaultArgTypeOnlyBase<B>()
enum class DefaultArgTypeOnlyEnum(val xs: List<String> = emptyList()) { ONE, TWO(listOf("a")) }

class DefaultArgNestB<X>(val x: X) { fun get(a: X = x): X = a }
class DefaultArgNestC<T>(val b: DefaultArgNestB<T>) { fun one(a: T = b.get()): T = a }
class DefaultArgNestD<U>(val c: DefaultArgNestC<U>) { fun two(a: U = c.one()): U = a }
class DefaultArgNestE<V>(val d: DefaultArgNestD<V>) { fun three(a: V = d.two()): V = a }

// #235: EVALUATION ORDER around a value bound for single evaluation. Binding one value moves its evaluation ahead of
// the call, so every side-effecting value to its LEFT must move with it — Kotlin evaluates the receiver, then each
// argument, left to right.
fun defaultArgOrder(p: Int, q: Int, r: Int = q * 10): Int = p * 10000 + q * 100 + r
class DefaultArgLog {
    var s = ""
    fun a(): Int { s += "a"; return 1 }
    fun b(): Int { s += "b"; return 2 }
    fun self(): DefaultArgLog { s += "m"; return this }
    fun m(p: Int, q: Int = p * 10): Int = p * 100 + q
}
class DefaultArgCell {
    var x = 0
    fun bump(): Int { x = 5; return 1 }
}

// A CROSS-MODULE data-class `copy` that omits a field. The frontend artifact preserves no default VALUE for a
// referenced callee, so kotc RECONSTRUCTS each omitted field as a read of the call's receiver; that read must take the
// receiver's single-evaluation temp. Reconstructing it as a second RENDERING of the receiver expression ran the
// receiver once per omitted field ON TOP of the call's own use of it. `kotlin.Pair`/`kotlin.Triple` come from the
// stdlib, so every `copy` below is cross-module by construction.
class DefaultArgCopyLog {
    var calls = 0
    var s = ""
    fun pair(): Pair<Int, Int> { calls++; s += "P"; return 1 to 2 }
    fun triple(): Triple<Int, Int, Int> { calls++; s += "T"; return Triple(1, 2, 3) }
    fun arg(): Int { s += "a"; return 9 }
}
// A receiver expression carrying a LAMBDA: a second rendering would lift a second copy of the lambda as well as re-run
// the call that takes it.
object DefaultArgCopyLambdaCounter { var calls = 0 }
fun defaultArgCopyVia(f: () -> Int): Pair<Int, Int> { DefaultArgCopyLambdaCounter.calls++; return f() to 2 }

// A data class may ALSO declare a differently-signed `copy` OVERLOAD of its own — only the generated signature is
// reserved. Its defaults are ordinary expressions, not `this.<field>` reads, so the synthetic-copy test must not claim
// it (`copy` + `isData` alone did). A CONTROL, not a regression test — it passes with the selector reverted, and the
// tightened selector is UNMEASURED: reaching the mis-selection needs a data class with a `copy` overload in a
// REFERENCED module, and the only cross-module source that preserves the `data` nature is the frontend KLIB, i.e. the
// stdlib, which declares no such class. What IS verified here is that the overload compiles, resolves and returns its
// own ordinary default.
data class DefaultArgCopyOver(val x: Int, val y: Int) {
    fun copy(tag: String, z: Int = x * 2): String = "$tag/$z/$y"
}

// A FILLED default that a later default reads is bound to a temp declared AHEAD of the call node. Kotlin evaluates the
// receiver, then each argument, and only then the callee's defaults — so binding it must not move it in front of them.
// The side-effecting default is a TOP-LEVEL call on purpose: a default calling a MEMBER of the callee's own class reads
// the dispatch receiver, which the pre-pass already bound for that reason, and would not exercise the ordering rule.
object DefaultArgFillLog {
    var s = ""
    fun mk(): Int { s += "d"; return 3 }
    fun arg(): Int { s += "p"; return 7 }
}
fun defaultArgFillMk(): Int = DefaultArgFillLog.mk()
class DefaultArgFillOrder {
    fun f(a: Int = defaultArgFillMk(), b: Int = a * 10): Int = a * 1000 + b
    fun g(p: Int, a: Int = defaultArgFillMk(), b: Int = a * 10): Int = p + a * 1000 + b
    // A supplied argument whose parameter index is HIGHER than the bound fill's: the fill's temp must still sort after
    // it, which a bare parameter-index key does not express.
    fun h(a: Int = defaultArgFillMk(), b: Int = a * 10, c: Int): Int = c + a * 1000 + b
}
fun defaultArgFillHost(): DefaultArgFillOrder { DefaultArgFillLog.s += "H"; return DefaultArgFillOrder() }
class DefaultArgFillCtorHost(val p: Int, val a: Int = defaultArgFillMk(), val b: Int = a * 10)

// An EXTENSION call site fills its omitted defaults through the same pass as every other call, and must run that pass
// ONCE. Filling is not a pure rendering — it binds a filled default that a LATER default reads to a temp — so a second
// run declared a SECOND temp holding the same default expression, and both initializers ran.
class DefaultArgExtSource { var calls = 0; fun bump(): Int { calls++; return 3 } }
fun DefaultArgExtSource.defaultArgExtChain(a: Int = bump(), b: Int = a * 10): Int = a * 1000 + b
class DefaultArgExtOwner(val k: Int) {
    fun DefaultArgExtSource.memChain(a: Int = bump(), b: Int = a * 10): Int = a * 1000 + b + k
    fun run(s: DefaultArgExtSource): Int = s.memChain()
}

// A member extension inside an INNER class whose filled default reads an ENCLOSING instance — the shape where a call
// has THREE live values (the enclosing chain, the dispatch receiver and the extension receiver) and the default reads
// the one the call site never writes. Every side effect is logged so the count AND the order are asserted.
object DefaultArgEncLog { var s = "" }
class DefaultArgEncSrc(val n: Int) { fun bump(): Int { DefaultArgEncLog.s += "s"; return n } }
fun defaultArgEncSrc(): DefaultArgEncSrc { DefaultArgEncLog.s += "S"; return DefaultArgEncSrc(2) }
class DefaultArgEncOuter(val k: Int) {
    fun mark(): Int { DefaultArgEncLog.s += "K"; return k }
    inner class DefaultArgEncInner {
        // `mark()` is a member of the ENCLOSING class, so this default reads `this@DefaultArgEncOuter` — reached from the
        // member extension's DISPATCH receiver, never from its extension receiver.
        fun DefaultArgEncSrc.encOnly(a: Int = mark(), b: Int = a * 10): Int = a * 1000 + b
        // …and one reading BOTH the enclosing instance and the extension receiver.
        fun DefaultArgEncSrc.encAndExt(a: Int = mark() + bump(), b: Int = a * 10): Int = a * 1000 + b
        fun goEncOnly(): Int = defaultArgEncSrc().encOnly()
        fun goEncAndExt(): Int = defaultArgEncSrc().encAndExt()
    }
    fun inner(): DefaultArgEncInner { DefaultArgEncLog.s += "I"; return DefaultArgEncInner() }
}

// §7a — the QUALIFIER of an `object`/companion call, at a call site that needs an evaluation plan.
//
// A plain `companion object` is flattened onto its enclosing class, so `DefaultArgFlat.make(…)` emits a receiver-less static
// call: the qualifier is not a value the emitted shape can hold, and the plan must not mint one for it (a binding
// nothing reads, holding a read of an `INSTANCE` field this representation never emits, is not an evaluation that
// was skipped — there is nothing there to evaluate). A real `object` keeps its singleton and its `INSTANCE` read,
// and a default that reads the object's own member makes the qualifier a second reader of it.
//
// These assert VALUES, not initialization ORDER: when a companion's initialization runs is the CLR type
// initializer's schedule (§7a), and this backend has separate, older gaps there — a flattened companion's `init { }`
// block is not emitted at all — which are not what this battery is about and must not be pinned here by accident.
class DefaultArgFlat {
    companion object {
        // A default reading an EARLIER parameter forces a plan at every call site, supplied or not.
        fun make(width: Int = 2, height: Int = width * 3): Int = width * 100 + height
    }
}

// …and the ORDER half of the same rule. A REAL object's `INSTANCE` load runs the object's own body, so it is an
// observable evaluation and Kotlin runs it BEFORE every argument. `DefaultArgOrd.take(defaultArgMark("A"))` must therefore log
// "O" then "A" — the qualifier needs a plan binding to hold that position, because the default reading an earlier
// parameter forces the argument into a pre-call local that would otherwise overtake it.
val defaultArgOrdLog = StringBuilder()
fun defaultArgMark(tag: String): Int { defaultArgOrdLog.append(tag); return tag.length }

object DefaultArgOrd {
    init { defaultArgOrdLog.append("O") }
    fun take(a: Int, b: Int = a * 2): Int = a * 10 + b
}

object DefaultArgSolo {
    val k = 4
    // Reads the object's own member, so the default binds the call's DISPATCH RECEIVER — the qualifier now has two
    // readers (the call's own receiver slot and this default) and is read in place at both.
    fun scale(a: Int = k, b: Int = a * k): Int = a + b
    // An inline member: the third receiver-binding site, where the qualifier reaches the payload rather than a slot.
    inline fun twice(n: Int, f: (Int) -> Int): Int = f(n) + f(n)
}

// #225: one default expression is rendered both into its metadata carrier and at a same-module omitting call.
// A local function in that expression must keep one lexical declaration identity rather than being registered twice
// (or emitted once with the second rendering left as an unresolved local-function call).
fun defaultArgLocalFunction(block: () -> Int = {
    fun answer(): Int = 17
    answer()
}): Int = block()

class DefaultArgumentTests {
    @TestAttribute
    fun defaultArguments() {
        val xs = listOf(1, 2, 3)
        assertEquals("x1-x2-x3", xs.joinToString("-") { "x$it" })                          // x1-x2-x3
        assertEquals("1, 2, 3", xs.joinToString())                                         // 1, 2, 3
        assertEquals("[1, 2, 3]", xs.joinToString(prefix = "[", postfix = "]"))            // [1, 2, 3]
        assertEquals("1/2/~", xs.joinToString(separator = "/", limit = 2, truncated = "~")) // 1/2/~
        assertEquals("b=c", "a=b=c".substringAfter("="))                                   // b=c
        assertEquals("a", "a=b=c".substringBefore("="))                                    // a
        assertEquals("nodelim", "nodelim".substringAfter("="))                             // nodelim
        assertEquals("FALLBACK", "nodelim".substringBefore("=", "FALLBACK"))               // FALLBACK
        val p = DefaultArgP(1, 2, 3)
        assertEquals("DefaultArgP(x=1, y=20, z=3)", p.copy(y = 20).toString())                     // was P(x=1, y=20, z=3)
        assertEquals("DefaultArgP(x=10, y=2, z=30)", p.copy(x = 10, z = 30).toString())            // was P(x=10, y=2, z=30)
        assertEquals("Hello, Kotlin!", defaultArgGreet("Kotlin"))                                  // Hello, Kotlin!
        assertEquals("Hello, Kotlin?", defaultArgGreet("Kotlin", punct = "?"))                     // Hello, Kotlin?
        assertEquals(17, defaultArgLocalFunction())
    }

    @TestAttribute
    fun defaultArguments2() {
        assertEquals(55, defaultArgF(5))        // 55
        assertEquals(7, defaultArgF(5, 2))      // 7
        assertEquals(12, defaultArgG(3))        // 12
        assertEquals(30, defaultArgG(3, 10))    // 30
        assertEquals(134, defaultArgH(1))       // 134
        assertEquals(156, defaultArgH(1, 5))    // 156
        assertEquals(159, defaultArgH(1, 5, 9)) // 159
    }

    // #235: a constructor default that reads an earlier constructor parameter is filled at the omitting `new`,
    // exactly as the function-path defaults above are — one default-filling pass serves both.
    @TestAttribute
    fun defaultArgumentsCtor() {
        assertEquals(6, DefaultArgRect(3).h)                                    // w * 2
        assertEquals(5, DefaultArgRect(3, 5).h)                                 // provided, not defaulted
        assertEquals("DefaultArgDRect(w=2, h=6, tag=d)", DefaultArgDRect(2).toString())
        assertEquals("DefaultArgDRect(w=2, h=6, tag=z)", DefaultArgDRect(2, tag = "z").toString())   // later arg by NAME keeps its slot
        assertEquals("DefaultArgDRect(w=4, h=6, tag=d)", DefaultArgDRect(2).copy(w = 4).toString())  // copy's own self-defaults still fill
        val t = DefaultArgTri(2)
        assertEquals(3, t.b)                                            // a + 1
        assertEquals(23, t.c)                                           // a * 10 + b, reading TWO earlier params
        val t2 = DefaultArgTri(2, 10)
        assertEquals(30, t2.c)                                          // a * 10 + b, b provided
        assertEquals(8, DefaultArgSec(1, 2).v)                                  // secondary ctor: c = a * 5 -> 1 + 2 + 5
        assertEquals(6, DefaultArgSec(1, 2, 3).v)                               // secondary ctor, c provided
        assertEquals(136, DefaultArgOwn(1).DefaultArgIn(3).all)                         // inner: enclosing instance prepended, b = a * 2
        assertEquals(134, DefaultArgOwn(1).DefaultArgIn(3, 4).all)                      // inner, b provided
        assertEquals(12, defaultArgLocalDefault(10))                            // local class: captures prepended, q = p + seed
    }

    // #235: the constructor call sites that used to bypass default filling entirely — a delegation and an enum entry.
    @TestAttribute
    fun defaultArgumentsCtorDelegation() {
        assertEquals(3, DefaultArgDel().a)                                      // `: this(3)`
        assertEquals(6, DefaultArgDel().b)                                      // omitted there: a * 2
        assertEquals(12, DefaultArgDel(4, 12).b)                                // the primary ctor still takes both
        assertEquals(2, DefaultArgDelSub().a)                                   // `: super(2)`
        assertEquals(3, DefaultArgDelSub().b)                                   // omitted there: a + 1
        assertEquals("c", DefaultArgHue.R.label)                                // enum entry `R(1)` omits label
        assertEquals(1, DefaultArgHue.R.rgb)
        assertEquals("g", DefaultArgHue.G.label)                                // provided
        assertEquals("op", DefaultArgOp.ADD.label)                              // entry BODY: the subclass's base() call omits it
        assertEquals(5, DefaultArgOp.ADD.apply(2, 3))
        assertEquals("neg", DefaultArgOp.NEG.label)                             // entry BODY, provided
        assertEquals(-2, DefaultArgOp.NEG.apply(2, 3))
    }

    // #235: a constructor default reading the enclosing instance, and one reading a parameter a closure captured.
    @TestAttribute
    fun defaultArgumentsCtorEnclosingInstance() {
        assertEquals(20, DefaultArgEncl(5).DefaultArgEIn().x)                           // k * 4, k from the enclosing instance
        assertEquals(1, DefaultArgEncl(5).DefaultArgEIn(1).x)                           // provided
        assertEquals(2, DefaultArgDeep(1).DefaultArgDMid().DefaultArgDIn().x)                   // q + 1, two levels up (via `__outer`)
        assertEquals(9, DefaultArgDeep(1).DefaultArgDMid().DefaultArgDIn(9).x)                  // provided
        assertEquals(6, defaultArgRec(3))                                       // 3 + (2 + (1 + 0))
        assertEquals(0, defaultArgRec(0))                                       // b = a * 2 = 0
        assertEquals(1, defaultArgRec(1))
        assertEquals(5050, DefaultArgSelfHolder().call())                       // a = this.k = 5, b = 50 (NOT c.k = 7)
        assertEquals(36, DefaultArgEnclHolder(DefaultArgEncl(9)).x())                   // k * 4 = 36, enclosing instance through `o`, not `this`
        assertEquals(7, DefaultArgEnclDel(7).In().b)                            // inner secondary ctor delegating
        assertEquals(2, DefaultArgEnclDel(7).In(1, 2).b)                        // provided
        assertEquals(20, DefaultArgEnclMember(5).Q().f())                       // MEMBER of an inner class: k * 4
        assertEquals(1, DefaultArgEnclMember(5).Q().f(1))                       // provided
        assertEquals(11, defaultArgLocalDelegate(11))                           // local class, secondary ctor delegating
    }

    // #235: a value a filled default SPLICES is evaluated exactly once — Kotlin evaluates a receiver and each
    // argument once, and the splice must not re-run it per reading default.
    @TestAttribute
    fun defaultArgumentsSingleEval() {
        val a = DefaultArgEvalOnce()
        assertEquals(44, defaultArgSideF(a.next()))                             // 4 + 40
        assertEquals(1, a.calls)                                        // the argument ran ONCE, not once per splice

        val b = DefaultArgEvalOnce()
        assertEquals(44, DefaultArgSideC(b.next()).sum)                         // the same through a `new`
        assertEquals(1, b.calls)

        val c = DefaultArgEvalOnce()
        assertEquals(23, DefaultArgTri(c.next() - 2).c)                         // a = 2, read by BOTH b and c defaults
        assertEquals(1, c.calls)

        val d = DefaultArgEvalOnce()
        assertEquals("h/h", d.holder().tag())                           // the RECEIVER a `= s` default reads
        assertEquals(1, d.calls)

        val e = DefaultArgEvalOnce()
        assertEquals(36, e.encl().DefaultArgEIn().x)                            // the ENCLOSING INSTANCE a `= k * 4` default reads
        assertEquals(1, e.calls)                                        // ctor and default see the SAME instance

        val f = DefaultArgEvalOnce()
        assertEquals(5, defaultArgSideF(f.next(), 1))                           // no default filled -> no temp, still once
        assertEquals(1, f.calls)

        val g = DefaultArgDefaultSource()
        assertEquals(3030, defaultArgDefaultChainF(g))                           // a = bump(), b = a * 10
        assertEquals(1, g.calls)                                        // the FILLED default itself ran once

        val h = DefaultArgDefaultSource()
        val hc = DefaultArgDefaultChainC(h)
        assertEquals(3, hc.a)
        assertEquals(30, hc.b)
        assertEquals(1, h.calls)                                        // the same rule through a constructor `new`

        val i = DefaultArgDefaultSource()
        val ic = DefaultArgDefaultChainDel(i, "")
        assertEquals(3, ic.a)
        assertEquals(30, ic.b)
        assertEquals(1, i.calls)                                        // and through a constructor delegation

        assertEquals(3, DefaultArgDefaultChainE.ONLY.a)
        assertEquals(30, DefaultArgDefaultChainE.ONLY.b)
        assertEquals(1, DefaultArgDefaultEnumSource.calls)                       // and through an enum-entry initializer
    }

    // #235: binding a value for single evaluation must not REORDER the call's other values.
    @TestAttribute
    fun defaultArgumentsEvalOrder() {
        val l = DefaultArgLog()
        assertEquals(10220, defaultArgOrder(l.a(), l.b()))                      // p=1, q=2, r=q*10=20
        assertEquals("ab", l.s)                                         // q is bound; p must still run FIRST

        val l2 = DefaultArgLog()
        assertEquals(110, l2.self().m(l2.a()))                          // p=1, q=p*10=10
        assertEquals("ma", l2.s)                                        // receiver before argument

        val c = DefaultArgCell()
        assertEquals(10550, defaultArgOrder(c.bump(), c.x))                     // p=1, then q reads x=5, r=50
    }

    // Single evaluation of the receiver a CROSS-MODULE data-class `copy` reconstructs its omitted fields from. Each
    // `calls`/`s` assertion is the load-bearing one — the copied VALUES were already right, the receiver just ran
    // repeatedly. The pre-fix counts are noted per case.
    @TestAttribute
    fun defaultArgumentsCrossModuleCopySingleEval() {
        val a = DefaultArgCopyLog()
        assertEquals("(1, 9)", a.pair().copy(second = 9).toString())     // (1, 9)
        assertEquals(1, a.calls)                                        // 1  was 2 (one omitted field + the call)

        val b = DefaultArgCopyLog()
        assertEquals("(1, 9, 3)", b.triple().copy(second = 9).toString())
        assertEquals(1, b.calls)                                        // 1  was 3 (two omitted fields + the call)

        val c = DefaultArgCopyLog()
        assertEquals("(1, 2, 3)", c.triple().copy().toString())          // every field omitted
        assertEquals(1, c.calls)                                        // 1  was 4

        // ORDER: Kotlin evaluates the receiver, then the argument. Re-rendering the receiver per omitted field also put
        // a receiver evaluation AFTER the argument (the log read "TTaT").
        val d = DefaultArgCopyLog()
        assertEquals("(1, 9, 3)", d.triple().copy(second = d.arg()).toString())
        assertEquals("Ta", d.s)                                         // Ta  receiver first, then the argument
        assertEquals(1, d.calls)

        // A receiver expression carrying a lambda: re-rendering it lifted a second copy of the lambda too.
        DefaultArgCopyLambdaCounter.calls = 0
        assertEquals("(7, 5)", defaultArgCopyVia { 7 }.copy(second = 5).toString())
        assertEquals(1, DefaultArgCopyLambdaCounter.calls)                       // 1  was 2

        // A data class's own `copy` OVERLOAD is a different function with ordinary defaults — the synthetic-copy test
        // must not claim it (name + `isData` alone did; the signature has to match the generated one).
        assertEquals("t/2/2", DefaultArgCopyOver(1, 2).copy("t"))                // t/2/2  z = x * 2, an ordinary default
        assertEquals("DefaultArgCopyOver(x=1, y=9)", DefaultArgCopyOver(1, 2).copy(y = 9).toString())
    }

    // A FILLED default bound for single evaluation must not move AHEAD of the values the call SUPPLIES: Kotlin
    // evaluates the receiver, then each argument, and only then the callee's defaults.
    @TestAttribute
    fun defaultArgumentsFilledDefaultOrder() {
        DefaultArgFillLog.s = ""
        assertEquals(3030, defaultArgFillHost().f())                            // a = mk() = 3, b = a * 10 = 30
        assertEquals("Hd", DefaultArgFillLog.s)                                 // Hd   receiver first (was "dH")

        DefaultArgFillLog.s = ""
        assertEquals(3037, defaultArgFillHost().g(DefaultArgFillLog.arg()))             // 7 + 3000 + 30
        assertEquals("Hpd", DefaultArgFillLog.s)                                // Hpd  receiver, argument, then the default (was "dHp")

        // The supplied argument sits at a HIGHER parameter index than the bound fill.
        DefaultArgFillLog.s = ""
        assertEquals(3037, defaultArgFillHost().h(c = DefaultArgFillLog.arg()))
        assertEquals("Hpd", DefaultArgFillLog.s)                                // Hpd  (was "Hdp" — the fill ran before the argument)

        DefaultArgFillLog.s = ""
        val c = DefaultArgFillCtorHost(DefaultArgFillLog.arg())
        assertEquals(3, c.a)
        assertEquals(30, c.b)
        assertEquals("pd", DefaultArgFillLog.s)                                 // pd   argument before the default (was "dp")
    }

    // The same single-evaluation rule at an EXTENSION call site, whose default-filling pass used to run twice.
    @TestAttribute
    fun defaultArgumentsSingleEvalExtensionCall() {
        val a = DefaultArgExtSource()
        assertEquals(3030, a.defaultArgExtChain())                              // a = bump() = 3, b = a * 10 = 30
        assertEquals(1, a.calls)                                        // 1  was 2 (the fill ran once per rendering)

        val b = DefaultArgExtSource()
        assertEquals(3031, DefaultArgExtOwner(1).run(b))                        // a MEMBER extension: both receivers live
        assertEquals(1, b.calls)                                        // 1  was 2
    }

    // A member extension in an INNER class whose filled default reads an ENCLOSING instance: the call has THREE live
    // values (the enclosing chain, the dispatch receiver and the extension receiver), and the default reads the one the
    // call site never writes. The enclosing read is reached from the DISPATCH receiver, and must be evaluated once and
    // after the values the call supplies. Both halves come from the plan: the enclosing instance is the dispatch
    // receiver's binding (so one evaluation, however many defaults read it), and the fill is a default-phase binding,
    // which the order rule keeps behind every supplied value.
    @TestAttribute
    fun defaultArgumentsEnclosingReadAtAMemberExtension() {
        DefaultArgEncLog.s = ""
        assertEquals(7070, DefaultArgEncOuter(7).inner().goEncOnly())           // a = mark() = 7, b = 70
        assertEquals("ISK", DefaultArgEncLog.s)                                 // ISK  inner, extension receiver, then the default (was "ISKK")

        DefaultArgEncLog.s = ""
        assertEquals(9090, DefaultArgEncOuter(7).inner().goEncAndExt())         // a = mark() + bump() = 7 + 2 = 9, b = 90
        assertEquals("ISKs", DefaultArgEncLog.s)                                // ISKs (was "ISKsKs")
    }

    // #235: single evaluation at the two call sites that ride a DECLARATION rather than an expression.
    @TestAttribute
    fun defaultArgumentsSingleEvalDelegationAndEnum() {
        DefaultArgDelCounter.calls = 0
        val d = DefaultArgDelOnce("")
        assertEquals(4, d.p)                                            // `: this(next())`
        assertEquals(40, d.q)                                           // p * 10, filled at the delegation
        assertEquals(1, DefaultArgDelCounter.calls)                             // next() ran ONCE, not once per splice

        DefaultArgDelCounter.calls = 0
        val s = DefaultArgDelSubOnce()
        assertEquals(4, s.p)                                            // `: super(next())`
        assertEquals(40, s.q)
        assertEquals(1, DefaultArgDelCounter.calls)

        // The entries are constructed by the enum's static initializer, so each counter reflects that ONE construction:
        // two entries, one `next()` each.
        assertEquals(4, DefaultArgEnumOnce.R.n)
        assertEquals(40, DefaultArgEnumOnce.R.m)                                // n * 10, filled at the entry
        assertEquals("r", DefaultArgEnumOnce.R.tag())
        assertEquals(40, DefaultArgEnumOnce.X.m)                                // filled at the per-entry body's base call
        assertEquals("x", DefaultArgEnumOnce.X.tag())
        assertEquals(2, DefaultArgEnumCounter.calls)                            // 2 entries x once (was 2 x twice)

        // ORDER at those same two call sites: the SUPPLIED value first, then the filled default.
        DefaultArgDelOrderLog.s = ""
        val o = DefaultArgDelOrder("")
        assertEquals(2, o.x); assertEquals(3, o.a); assertEquals(30, o.b)
        assertEquals("pd", DefaultArgDelOrderLog.s)                             // pd   `: this(p())` before the target's default

        DefaultArgDelOrderLog.s = ""
        val ob = DefaultArgDelOrderSub()
        assertEquals(2, ob.x); assertEquals(3, ob.a); assertEquals(30, ob.b)
        assertEquals("pd", DefaultArgDelOrderLog.s)                             // pd   `: super(p())` before the base's default
    }

    // A fill sitting in a slot BEFORE the slot the call supplies: the emitted argument array's order is NOT Kotlin's,
    // so the call needs an evaluation plan even though no value is read twice.
    @TestAttribute
    fun defaultArgumentsFillBeforeASuppliedSlot() {
        DefaultArgSlotOrderLog.s = ""
        assertEquals(3007, defaultArgSlotOrder(c = DefaultArgSlotOrderLog.p()))         // a = 3, c = 7
        assertEquals("pd", DefaultArgSlotOrderLog.s)                            // pd   the argument, then the default (was "dp")

        // The sibling where a later default also reads the fill — correct before, and it must stay correct.
        DefaultArgSlotOrderLog.s = ""
        assertEquals(3037, defaultArgSlotOrderChain(c = DefaultArgSlotOrderLog.p()))    // a = 3, b = 30, c = 7
        assertEquals("pd", DefaultArgSlotOrderLog.s)                            // pd

        DefaultArgSlotOrderLog.s = ""
        val k = DefaultArgSlotOrderCtor(c = DefaultArgSlotOrderLog.p())
        assertEquals(3, k.a); assertEquals(7, k.c)
        assertEquals("pd", DefaultArgSlotOrderLog.s)                            // pd   a constructor is the same call site
    }

    // A generic base's chained constructor defaults, bound in a derived class whose type-parameter frame differs. The
    // bound local must be typed in the frame it LIVES in; the base's `T` names a different slot there.
    @TestAttribute
    fun defaultArgumentsGenericBaseDelegationChain() {
        assertEquals("7/7/7", DefaultArgGenDerived<String, Int>(7).probe())     // 7/7/7  (was InvalidProgramException)
        assertEquals("k/k/k", DefaultArgGenDerived<Int, String>("k").probe())   // k/k/k
    }

    // Splicing a default into a caller closes EVERY open type variable it mentions, across everything a default may
    // read. Each of the first four was an InvalidProgramException at load; the last two are controls.
    @TestAttribute
    fun defaultArgumentsCloseCalleeTypeFrame() {
        assertEquals("7", DefaultArgFrameOwnerProp(7).one())                    // the receiver's property
        assertEquals("s", DefaultArgFrameOwnerProp("s").one())                  // ...at a second instantiation
        assertEquals("t7", DefaultArgFrameOwnerCall(7).one())                   // a member CALL on the receiver
        val c = DefaultArgFrameOwnerCtor(7)
        assertEquals(7, c.w); assertEquals(7, c.x)                      // the receiver inside a generic ctor's default
        assertEquals("77", DefaultArgFrameOwnerChain(7).pair())                 // ...chained into a later default
        assertEquals("7", 7.defaultArgFrameExt())                               // an EXTENSION receiver
        assertEquals("k", "k".defaultArgFrameExt())
        assertEquals("u7", DefaultArgFrameOwnAndOwner(7).two("u"))              // the callee's own type param beside the owner's
        assertEquals("75", DefaultArgFrameControls(7).konst())                  // CONTROL: a const default
        assertEquals("736", DefaultArgFrameControls(7).prior(3))                // CONTROL: a prior-param default

        // ...at nesting depth 1, 2 and 3: a default filling a default filling a default. Each frame closes against
        // the one it is spliced into, so the substitutions compose all the way out to the call site.
        assertEquals(7, DefaultArgNestC(DefaultArgNestB(7)).one())                      // depth 1
        assertEquals("s", DefaultArgNestC(DefaultArgNestB("s")).one())
        assertEquals(7, DefaultArgNestD(DefaultArgNestC(DefaultArgNestB(7))).two())             // depth 2
        assertEquals(7, DefaultArgNestE(DefaultArgNestD(DefaultArgNestC(DefaultArgNestB(7)))).three())  // depth 3
        assertEquals("z", DefaultArgNestE(DefaultArgNestD(DefaultArgNestC(DefaultArgNestB("z")))).three())

        // ...and where the default's EXPRESSION reads nothing at all and only its TYPES are the callee's.
        assertEquals(0, DefaultArgTypeOnly(7).ownerTv())                        // the owner's type parameter
        assertEquals(0, DefaultArgTypeOnly(7).funTv<String>())                  // the callee's own
        assertEquals(0, DefaultArgTypeOnly(7).mixed<String>())                  // both
        assertEquals(1, DefaultArgTypeOnly(7).nested())                         // a later default reading the first
        assertEquals(0, DefaultArgTypeOnlySub<Int, String>().n)                 // a `: super(…)` delegation
        assertEquals(0, DefaultArgTypeOnlyEnum.ONE.xs.size)                     // an enum entry
        assertEquals(1, DefaultArgTypeOnlyEnum.TWO.xs.size)
    }

    // A default's `this` read binds per RECEIVER KIND. Each assertion is tagged with what it was before the
    // kind-directed binding: WAS-NRE threw a NullReferenceException (the default read a dispatch-owner member off
    // the extension receiver's VALUE), CONTROL passed already and must keep passing.
    @TestAttribute
    fun defaultArgumentsReceiverKind() {
        val h = DefaultArgRecvKind(10)
        assertEquals(30, h.run())                                       // WAS-NRE  3 * dispatch k=10
        assertEquals(30, h.runInline())                                 // WAS-NRE  lambda-less `inline`: ordinary path
        assertEquals(30, h.runCarrier())                                // carrier: dispatch and extension stay distinct
        assertEquals(21, h.runParam())                                  // CONTROL  3 * 7 — the value-param arm
        assertEquals(15, DefaultArgRecvOuter(5).R().run())                      // WAS-NRE  3 * OUTER k=5, enclosing chain
        assertEquals(15, DefaultArgRecvOuter(5).R().runCarrier())               // carrier: outer chain roots at dispatch
        assertEquals(9, 3.defaultArgSelfScaled())                               // CONTROL  3 * extension receiver 3
    }

    /** §7a — an `object`/companion qualifier at a call site that carries an evaluation plan. */
    @TestAttribute
    fun defaultArgumentsObjectQualifierIsNotAPlanValue() {
        // FLATTENED companion: the emitted static call has no receiver slot at all.
        assertEquals(206, DefaultArgFlat.make())                                // width=2, height=2*3
        assertEquals(515, DefaultArgFlat.make(5))                               // height still filled from the supplied width
        assertEquals(202, DefaultArgFlat.make(height = 2))                      // named-middle omission: `width` fills to 2

        // REAL singleton: the qualifier IS a value, read by the call's receiver slot AND by the default that reads
        // the object's own member. Reading it twice is free — it is the same singleton either way.
        assertEquals(20, DefaultArgSolo.scale())                                // a=k=4, b=4*4
        assertEquals(15, DefaultArgSolo.scale(3))                               // a=3, b=3*4
        assertEquals(10, DefaultArgSolo.scale(b = 6))                           // named-middle omission: a fills to k=4
        assertEquals(30, DefaultArgSolo.twice(3) { it * 5 })                    // the inline site, qualifier in the payload
    }

    /** §7a — a REAL object's qualifier is an observable evaluation, and Kotlin runs it before the arguments. */
    @TestAttribute
    fun defaultArgumentsRealObjectQualifierIsEvaluatedBeforeTheArguments() {
        defaultArgOrdLog.setLength(0)
        assertEquals(12, DefaultArgOrd.take(defaultArgMark("A")))       // a=1 ("A".length), b=2
        // "AO" would mean the object was initialized only when the call finally touched it — after the argument's
        // side effect, and after an initializer that throws would have had to run.
        assertEquals("OA", defaultArgOrdLog.toString())
    }
}
