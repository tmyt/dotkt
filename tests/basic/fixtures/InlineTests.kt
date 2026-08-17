// Inline battery — migrates the non-coroutine `inline fun` family (cases/il-inline* + il-xinline). Each old
// case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assertEquals is strictly
// stronger (typed, fails the exact broken contract) and self-documenting. Every value the old il_check asserted
// is preserved 1:1 (see the `// <expected>` comments). Interior `println`s that were part of a case's contract
// (body-execution side effects, control-flow order) are captured into a log/list and asserted in order, so no
// coverage is lost when `main`/stdout disappears.
//
// EXCLUDED (belongs in a later coroutine battery, NOT here): cases/il-inline-suspend (suspend/Task determinism).
//
// Coverage preserved (old case -> method):
//   il-inline               -> inline_valueInline       non-reified value inline (twice/clamp)
//   il-inline2              -> inline2_nlrCapture        non-local return + value inline + mutable capture
//   il-xinline              -> xinline_crossinline       crossinline lambda in a nested/deferred context
//   il-inlinedefaultlambda  -> inlineDefaultLambda       #34 omitted defaulted param filled by splice (3 default kinds)
//   il-inlinememberdefault  -> inlineMemberDefault       #34 residual: MEMBER inline fn non-const defaulted arg carried
//   il-inline-klibmember-nlr-> inlineKlibMemberNlr       #60 cross-module klib inline MEMBER + non-local return
//   il-inlineinherit        -> inlineInherit             #87 MEMBER inline fn inherited (fake override) + non-local return
//   il-inline-nested-nlr    -> inlineNestedNlr           #75 §8.1 a{b{return}} predicate-descent trap
//   il-inline-outerlabel    -> inlineOuterLabel          #75 §8.2 run outer@{ forEach{ return@outer } }
//   il-inline-nlbreak       -> inlineNlBreak             #75 §8.3 non-local break through a carrier + §4.1 hygiene
//   il-inline-ownlabel      -> inlineOwnLabel            #75 §8.4 forEach{ return@forEach } local -> delegate path
//   il-inline-mutcapture    -> inlineMutCapture          #75 §8.5 var write-through on the delegate path (ref-cell)
//   il-inline-forward       -> inlineForward             #75 §8.6 filter->filterTo forwarding + escaping return
//   il-inlinereturnexpr     -> inlineReturnExpr          #30 EXPRESSION-position return (elvis/if/when-as-value)
//   il-inlinereturnunit     -> inlineReturnUnit          #31 EXPRESSION-position `return unitFn()` must evaluate
//   il-inlinereturnlocal    -> inlineReturnLocal         #31 lambda-LOCAL labeled return@label in expr position
//   il-inlineretcoerce      -> inlineRetCoerce           inline-splice item 21: value-type-nullable smart-cast return
//
// Regression coverage added after the migration:
//   #285                    -> inlineTryBodyInOperand     inline try-expression body spliced into concat operand 0
//
// Top-level names are unique within this single battery assembly (one project = one namespace). Collisions with
// GenericsTests (`Box`) and within the family (`twice`, `runIt`, `pick`, `Box`) are renamed with a case suffix.
import kotlin.time.Duration.Companion.seconds
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull

// ---- il-inline : non-reified value inline (no reified T / non-local return / mutable capture) ----------------
inline fun twice(x: Int, f: (Int) -> Int): Int = f(f(x))
inline fun clamp(x: Int, lo: Int, hi: Int): Int = if (x < lo) lo else if (x > hi) hi else x

// ---- il-inline2 : Unit inline + NON-LOCAL return, value inline, mutable capture -----------------------------
inline fun forEach3(a: Int, b: Int, c: Int, action: (Int) -> Unit) { action(a); action(b); action(c) }
fun findFirstEven(): Int {
    forEach3(1, 3, 4) { if (it % 2 == 0) return it }   // returns from findFirstEven, not the lambda
    return -1
}
inline fun runBlock(block: () -> Int): Int = block()
fun computed(): Int = runBlock { 6 * 7 }
inline fun repeat3(action: (Int) -> Unit) { action(0); action(1); action(2) }
fun sum3(): Int { var total = 0; repeat3 { total = total + it }; return total }

// #285: InlineSplice leaves the try body and lambda binding in one ordered value block; TryValueOperandHoist must
// move its protected region ahead of the String.Concat operand accumulator.
inline fun inlineTry285(block: () -> Int): Int = try { block() } catch (e: Exception) { -1 }

// ---- il-xinline : crossinline lambda invoked from inside a nested (deferred) lambda --------------------------
inline fun xTwice(crossinline block: () -> Unit) { val r = { block() }; r(); r() }
inline fun xWrap(crossinline f: () -> Int): Int { val g = { f() + 1 }; return g() }
inline fun xMixed(direct: () -> Int, crossinline deferred: () -> Int): Int { val g = { deferred() }; return direct() + g() }

// ---- il-inlinedefaultlambda : #34 splice fills an OMITTED defaulted param (lambda/const/earlier-param) --------
inline fun withFallback(value: () -> Int, fallback: () -> Int = { 100 }): Int = value() + fallback()
inline fun scaled(n: Int = 3, body: (Int) -> Int): Int = body(n)
inline fun offset(base: Int, delta: Int = base * 10, body: (Int) -> Int): Int = body(base + delta)
inline fun nested(value: () -> Int, fallback: () -> Int = { listOf(10, 20, 30).count { it > 15 } }): Int = value() + fallback()

// ---- il-inlinememberdefault : #34 residual — MEMBER inline fn non-const defaulted arg carried ----------------
class MemDefBox(val base: Int) {
    inline fun choose(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int = if (cond) primary() else fallback()
    inline fun total(extra: List<Int> = emptyList(), body: (List<Int>) -> Int): Int = body(extra)
    inline fun scale(factor: Int = 4, body: (Int) -> Int): Int = body(base * factor)
    fun a(): Int = choose(true, { 5 })
    fun b(): Int = choose(false, { 5 })
    fun c(): Int = total { it.size }
    fun d(): Int = total(listOf(1, 2, 3)) { it.size }
    fun e(): Int = scale { it + 1 }
    fun f(): Int = scale(10) { it + 1 }
}

// ---- il-inline-klibmember-nlr : #60 cross-module klib inline MEMBER + non-local return -----------------------
fun pickKlib(): Int {
    val d = 3661.seconds          // 1h 1m 1s
    d.toComponents { hours, minutes, _, _ ->
        if (hours > 0L) return hours.toInt()   // NON-LOCAL return -> must exit pickKlib()
        return minutes
    }
    return -1                     // reached ONLY if the non-local return wrongly exits the delegate
}

// ---- il-inlineinherit : #87 MEMBER inline fn inherited via a fake override + non-local return ----------------
private val INHERIT_CLOSED = Any()
internal open class InhBase {
    var slot: Any? = null
    inline fun firstOr(onClosed: () -> Nothing): Any? {
        val cur = slot
        return if (cur === INHERIT_CLOSED) onClosed() else cur
    }
    fun headOrNull(): Any? = firstOr { return null }
}
internal class InhDerived : InhBase()
internal abstract class InhSeg<S : InhSeg<S>> : InhBase()
internal class InhConcreteSeg : InhSeg<InhConcreteSeg>()
internal fun probeDerived(d: InhDerived): Any? = d.firstOr { return "closed" }
internal fun <S : InhSeg<S>> probeSeg(cur: S): Any? = cur.firstOr { return "seg-closed" }

// ---- il-inline-nested-nlr / outerlabel / nlbreak : control-flow probes returning the captured trace ----------
fun nestedNlrProbe(): String {
    listOf(1, 2, 3).forEach { a ->
        listOf(10, 20).forEach { b ->
            if (a == 2 && b == 20) return "hit $a $b"   // non-local return exits nestedNlrProbe (NOT "after")
        }
    }
    return "after"
}
fun outerLabelProbe(): String {
    val out = mutableListOf<String>()
    run outer@{
        listOf(1, 2, 3).forEach {
            if (it == 2) return@outer
            out.add(it.toString())
        }
        out.add("unreached")
    }
    out.add("after")
    return out.joinToString(",")
}
fun nlBreakProbe(): String {
    val out = mutableListOf<String>()
    outer@ for (x in 1..3) {
        listOf(10, 20, 30).forEach {
            if (it == 20) return@forEach                 // local continue-like
            if (x == 2 && it == 30) break@outer          // non-local break to the outer for
            out.add("$x:$it")
        }
    }
    out.add("done")
    return out.joinToString("|")
}

// ---- il-inline-forward : #75 §8.6 filter->filterTo forwarding + escaping non-local return --------------------
fun evens(xs: List<Int>): String = xs.filter {
    if (it < 0) return "neg"
    it % 2 == 0
}.joinToString(",")

// ---- il-inlinereturnexpr : #30 EXPRESSION-position return; interior body prints -> reLog -----------------------
val reLog = mutableListOf<String>()
inline fun <R> elvisImpl(input: String?, onClosed: () -> R): R {
    val x: String = input ?: return onClosed()
    reLog.add("elvis-body $x")
    return onClosed()
}
inline fun <R> ifImpl(c: Boolean, onClosed: () -> R): R {
    val x: Int = if (c) 1 else return onClosed()
    reLog.add("if-body $x")
    return onClosed()
}
inline fun <R> whenImpl(k: Int, onClosed: () -> R): R {
    val x: Int = when (k) {
        0 -> 10
        else -> return onClosed()
    }
    reLog.add("when-body $x")
    return onClosed()
}
inline fun exprBodyImpl(input: Int?, onClosed: () -> Int): Int = input ?: return onClosed()

// ---- il-inlinereturnunit : #31 EXPRESSION-position `return unitFn()` must EVALUATE the Unit call --------------
var ruCounter = 0
fun ruBump() { ruCounter++ }
val ruLog = mutableListOf<String>()
inline fun ruElvisUnit(input: String?, block: () -> Unit) {
    val x: String = input ?: return ruBump()     // expr-position elvis RHS, Unit-typed return
    block()
    ruLog.add("elvis-body $x")
}
inline fun ruIfUnit(c: Boolean, block: () -> Unit) {
    val x: Int = if (c) 1 else return ruBump()   // expr-position if-as-value, Unit-typed return
    block()
    ruLog.add("if-body $x")
}

// ---- il-inlinereturnlocal : #31 lambda-LOCAL labeled return@label in EXPRESSION position ---------------------
inline fun <R> deferred(crossinline block: () -> R): () -> R = { block() }
fun classifyDeferred(n: Int): Int {
    val f = deferred {
        val x: Int = if (n >= 0) n else return@deferred -1   // lambda-local, expr position (if-as-value)
        x * 2
    }
    return f()
}
inline fun <R> runIt(block: () -> R): R = block()
fun classifyRun(s: String?): String = runIt {
    val v: String = s ?: return@runIt "was-null"             // lambda-local, expr position (elvis RHS)
    "got-$v"
}

// ---- il-inlineretcoerce : value-type-nullable (Int?/UInt?) smart-cast return through the splice result-local -
inline fun <R> coerceRun(block: () -> R): R = block()
class CoerceBox(val p: Int?)
fun coerceSrc(): Int? = 7
fun paramSc(q: Int?): Int = coerceRun { if (q != null) return@coerceRun q; -1 }
fun propSc(b: CoerceBox): Int = coerceRun { if (b.p != null) return@coerceRun b.p; -1 }
fun localSc(): Int = coerceRun { val g: Int? = coerceSrc(); if (g != null) return@coerceRun g; -1 }
fun elvisCoerce(q: Int?): Int = coerceRun { return@coerceRun (q ?: -1) }
fun uintSc(u: UInt?): UInt = coerceRun { if (u != null) return@coerceRun u; 0u }
fun <T : Any> pickCoerce(x: T?, d: T): T = coerceRun { if (x != null) return@coerceRun x; d }

// A forwarded inline parameter can be materialized independently by more than one nested inline call. Each
// materialization is a distinct BIR declaration graph, including any local function declared inside the lambda.
inline fun <R> keepDeferred(crossinline block: () -> R): () -> R = { block() }
inline fun <R> keepDeferredTwice(crossinline block: () -> R): Pair<() -> R, () -> R> =
    Pair(keepDeferred(block), keepDeferred(block))

fun materializedLocalFunctionPair(): Pair<Int, Int> {
    val pair = keepDeferredTwice {
        fun localValue(): Int = 73
        localValue()
    }
    return Pair(pair.first(), pair.second())
}

// A tail return in an inline carrier is not necessarily the carrier's own return. With `?.let`, Kotlin lowers
// `return it` as the lambda's tail IrReturn targeting the enclosing function. The emitter must preserve that
// non-local control transfer instead of treating `it` as the carrier result and then falling through.
fun safeCallTailNlr(value: Int?): Int {
    value?.let { return it }
    return -1
}

// ---- #23 : SAME-MODULE inline member-extension whose body reads BOTH receivers (dispatch + extension) ---------
// `Int.scaledBy` is a member-extension of `Scaler` (dispatch = the Scaler instance, reads `factor`) on an `Int`
// (extension = `this`, rides `__self`). The body reads the extension receiver (`this`) AND the enclosing dispatch
// `this@Scaler.factor` (a `{k:this}` field read). Co-binding both receivers is #23; before the fix kotc failed loud
// (bodyReferencesDispatch) so no valid callInline was emitted.
class Scaler(val factor: Int) {
    inline fun Int.scaledBy(op: (Int) -> Int): Int = op(this) * factor   // (op applied to the Int) * the Scaler's factor
    fun run5(): Int = 5.scaledBy { it + 1 }                              // (5+1)*factor — same-module co-bind
    fun runNeg(): Int = (-2).scaledBy { it - 1 }                        // (-2-1)*factor
}
// A GENERIC dispatch owner (exercises dispatchTypeArgs on the co-bound `this` temp): the body reads the extension
// receiver (the Int) AND the dispatch's generic field `tag: T`.
class Tagger23<T>(val tag: T) {
    inline fun Int.tagged(op: (Int) -> String): String = op(this) + tag
    fun make(): String = 7.tagged { "v$it-" }                           // "v7-" + tag
}

// ---- #126 : hygienic free-capture under nested crossinline + name shadowing --------------------------------
// A caller-captured `x` colliding with an inner crossinline HOST carrier param `x`. The host carrier
// `{ x -> act() + x }` escapes into `d` (materializes), so act()'s freed capture `x` (the caller's) must be
// alpha-converted to a distinct field — NOT silently rebound to the host param `x`. Before the fix
// PropagateSplicedCaptures skipped the collision and the caller's `x` bound to the host param (7) -> 14.
inline fun hyg126MapDeferred(crossinline t: (Int) -> Int): Int {
    val d = { t(7) }          // t escapes -> materializes; host carrier param `x` becomes an invoke param
    return d()
}
inline fun hyg126Outer(crossinline act: () -> Int): Int = hyg126MapDeferred { x -> act() + x }
fun hyg126Caller(): Int {
    val x = 100
    return hyg126Outer { x }  // act = { x } captures caller x=100  -> 100 + 7 = 107  (bug: 7 + 7 = 14)
}
// DEPTH-2 chained: a value-capture-bearing former host is itself spliced into a SECOND host — the caller's `x` must
// ride the alpha-converted descriptor through both `x`-shadowing crossinline hosts to the caller frame.
inline fun hyg126L1(crossinline g: (Int) -> Int): Int { val d = { g(5) }; return d() }  // materializes
inline fun hyg126L2(crossinline f: (Int) -> Int): Int = hyg126L1 { x -> f(1) + x }      // host2 param x=5
inline fun hyg126L3(crossinline act: () -> Int): Int = hyg126L2 { x -> act() + x }      // host1 param x=1
fun hyg126CallerChained(): Int {
    val x = 100
    return hyg126L3 { x }     // act = { x } captures caller x=100  -> (100 + 1) + 5 = 106
}

// A cross-module inline/F-bound lowering can leave `next === null` over a generic-parameter local. CLR `ceq`
// cannot compare an unboxed generic slot with null; bir2cir must author the boxed object comparison in CIR.
interface GenericNullNode<N : GenericNullNode<N>> {
    val next: N?
}

class GenericNullLink(override val next: GenericNullLink?) : GenericNullNode<GenericNullLink>

fun <N : GenericNullNode<N>> genericNullTail(start: N): N {
    var current = start
    while (true) {
        val next = current.next
        if (next === null) return current
        current = next
    }
}

class InlineTests {
    @TestAttribute
    fun forwardedCarrierLocalFunctionIds() {
        assertEquals(Pair(73, 73), materializedLocalFunctionPair())
    }

    @TestAttribute
    fun valueInline() {
        assertEquals(5, twice(3) { it + 1 })   // f(f(3)) = 5
        assertEquals(40, twice(10) { it * 2 }) // f(f(10)) = 40
        assertEquals(3, clamp(5, 0, 3))        // 3
        assertEquals(0, clamp(-1, 0, 3))       // 0
    }

    @TestAttribute
    fun inlineTryBodyInOperand() {
        val success = inlineTry285 { 1 }.toString() + "/"
        val failure = inlineTry285 { throw RuntimeException("boom") }.toString() + "/"
        assertEquals("1/", success)
        assertEquals("-1/", failure)
    }

    @TestAttribute
    fun nlrCapture() {
        assertEquals(4, findFirstEven())   // 4  (non-local return from the lambda exits findFirstEven)
        assertEquals(42, computed())       // 42 (value inline)
        assertEquals(3, sum3())            // 0+1+2 = 3 (mutable capture spliced inline)
    }

    @TestAttribute
    fun crossinline() {
        var n = 0
        xTwice { n += 10 }                     // crossinline lambda mutating a captured var, twice
        assertEquals(20, n)                    // 20
        assertEquals(42, xWrap { 41 })         // 41 + 1 = 42
        assertEquals(105, xMixed({ 5 }, { 100 })) // 5 + (100 + 1) = 105
    }

    @TestAttribute
    fun inlineDefaultLambda() {
        assertEquals(105, withFallback({ 5 }))        // 5 + 100 = 105  (lambda default taken)
        assertEquals(6, withFallback({ 5 }, { 1 }))   // 5 + 1   = 6    (lambda default overridden)
        assertEquals(6, scaled { it * 2 })            // 3 * 2   = 6    (const default taken)
        assertEquals(20, scaled(10) { it * 2 })       // 10 * 2  = 20   (const default overridden)
        assertEquals(22, offset(2) { it })            // 2 + 20  = 22   (earlier-param default taken)
        assertEquals(7, offset(2, 5) { it })          // 2 + 5   = 7    (earlier-param default overridden)
        assertEquals(3, nested({ 1 }))                // 1 + count{>15}=2 = 3 (nested-inline default lambda taken)
        assertEquals(10, nested({ 1 }, { 9 }))        // 1 + 9   = 10   (nested-inline default lambda overridden)
    }

    @TestAttribute
    fun inlineMemberDefault() {
        val box = MemDefBox(2)
        assertEquals(5, box.a())    // 5
        assertEquals(-1, box.b())   // -1
        assertEquals(0, box.c())    // emptyList().size = 0
        assertEquals(3, box.d())    // 3
        assertEquals(9, box.e())    // 2*4 + 1 = 9
        assertEquals(21, box.f())   // 2*10 + 1 = 21
    }

    @TestAttribute
    fun inlineKlibMemberNlr() {
        assertEquals(1, pickKlib())   // hours=1 > 0 -> 1 (delegate-return bug would give -1)
    }

    @TestAttribute
    fun inlineInherit() {
        val d = InhDerived()
        d.slot = INHERIT_CLOSED; assertEquals("closed", probeDerived(d))   // closed (non-local return via fake override)
        d.slot = "D";            assertEquals("D", probeDerived(d))        // D (else branch)
        val s = InhConcreteSeg()
        s.slot = INHERIT_CLOSED; assertEquals("seg-closed", probeSeg(s))   // seg-closed
        s.slot = "S";            assertEquals("S", probeSeg(s))            // S
        assertNull(InhBase().headOrNull())                                 // True: own-class self-call, unset
    }

    @TestAttribute
    fun inlineNestedNlr() {
        // The non-local return must exit at "hit 2 20"; "after" must NOT be reached (the predicate-descent trap).
        assertEquals("hit 2 20", nestedNlrProbe())   // hit 2 20
    }

    @TestAttribute
    fun inlineOuterLabel() {
        // run outer@{ forEach{ return@outer } }: prints 1, exits the run lambda, then "after" runs.
        assertEquals("1,after", outerLabelProbe())   // 1 ; after
    }

    @TestAttribute
    fun inlineNlBreak() {
        // forEach{ break@outer }: 1:10, 1:30, 2:10, then break@outer, then done.
        assertEquals("1:10|1:30|2:10|done", nlBreakProbe())   // 1:10 ; 1:30 ; 2:10 ; done
    }

    @TestAttribute
    fun inlineOwnLabel() {
        // forEach{ return@forEach }: local continue-like -> delegate path; accumulate positives.
        var kept = 0
        listOf(1, -2, 3, -4, 5).forEach {
            if (it < 0) return@forEach
            kept += it
        }
        assertEquals(9, kept)   // 1 + 3 + 5 = 9
    }

    @TestAttribute
    fun inlineMutCapture() {
        // var write-through from a NON-escaping forEach lambda (delegate path ref-cell-boxes sum/count).
        var sum = 0
        var count = 0
        listOf(10, 20, 30).forEach { sum += it; count++ }
        assertEquals(60, sum)     // 60
        assertEquals(3, count)    // 3
    }

    @TestAttribute
    fun inlineForward() {
        assertEquals("2,4", evens(listOf(1, 2, 3, 4)))   // 2,4
        assertEquals("neg", evens(listOf(1, -5, 4)))     // neg (escaping non-local return via forwarded filterTo)
    }

    @TestAttribute
    fun inlineReturnExpr() {
        reLog.clear()
        assertEquals(5, elvisImpl(null) { 5 })       // early (expr-position return): onClosed -> 5
        assertEquals(6, elvisImpl("hi") { 6 })       // fall-through: elvis-body hi, then 6
        assertEquals(7, ifImpl(false) { 7 })         // early: onClosed -> 7
        assertEquals(8, ifImpl(true) { 8 })          // fall-through: if-body 1, then 8
        assertEquals(9, whenImpl(1) { 9 })           // early: onClosed -> 9
        assertEquals(11, whenImpl(0) { 11 })         // fall-through: when-body 10, then 11
        assertEquals(12, exprBodyImpl(null) { 12 })  // nested-in-tail-return early: onClosed -> 12
        assertEquals(4, exprBodyImpl(4) { 0 })       // fall-through: input -> 4
        // Interior body prints executed only on the fall-through path, in order (was: elvis-body hi / if-body 1 / when-body 10).
        assertEquals("elvis-body hi|if-body 1|when-body 10", reLog.joinToString("|"))
    }

    @TestAttribute
    fun inlineReturnUnit() {
        ruCounter = 0; ruLog.clear()
        ruElvisUnit(null) { ruLog.add("blk") }    // early -> ruBump(); counter 0->1, block NOT run
        assertEquals(1, ruCounter)                // counter=1
        ruElvisUnit("hi") { ruLog.add("blk2") }   // fall-through: block runs, elvis-body hi
        assertEquals(1, ruCounter)                // counter=1
        ruIfUnit(false) { ruLog.add("blk3") }     // early -> ruBump(); counter 1->2, block NOT run
        assertEquals(2, ruCounter)                // counter=2
        ruIfUnit(true) { ruLog.add("blk4") }      // fall-through: block runs, if-body 1
        assertEquals(2, ruCounter)                // counter=2
        // Only the fall-through paths ran their block+body (blk/blk3 dropped by the early return).
        assertEquals("blk2|elvis-body hi|blk4|if-body 1", ruLog.joinToString("|"))
    }

    @TestAttribute
    fun inlineReturnLocal() {
        assertEquals(10, classifyDeferred(5))    // 10 (materialized carrier)
        assertEquals(-1, classifyDeferred(-3))   // -1
        assertEquals("got-hi", classifyRun("hi")) // got-hi (direct-invoke carrier)
        assertEquals("was-null", classifyRun(null)) // was-null
    }

    @TestAttribute
    fun inlineRetCoerce() {
        assertEquals(5, paramSc(5))          // 5
        assertEquals(-1, paramSc(null))      // -1
        assertEquals(3, propSc(CoerceBox(3))) // 3
        assertEquals(-1, propSc(CoerceBox(null))) // -1
        assertEquals(7, localSc())           // 7
        assertEquals(9, elvisCoerce(9))      // 9
        assertEquals(-1, elvisCoerce(null))  // -1
        assertEquals(11u, uintSc(11u))       // 11
        assertEquals(0u, uintSc(null))       // 0
        assertEquals(4, pickCoerce(4, -1))   // 4
    }

    @TestAttribute
    fun inlineSafeCallTailNlr() {
        assertEquals(7, safeCallTailNlr(7))
        assertEquals(-1, safeCallTailNlr(null))
    }

    @TestAttribute
    fun inlineMemberExtDualReceiver() {
        // #23: an inline member-extension whose body reads BOTH the dispatch (enclosing-class) and the extension receiver.
        assertEquals(18, Scaler(3).run5())      // (5+1)*3 = 18
        assertEquals(-30, Scaler(10).runNeg())  // (-2-1)*10 = -30
        assertEquals("v7-X", Tagger23("X").make())  // "v7-" + "X"  (generic dispatch owner)
        assertEquals("v7-9", Tagger23(9).make())    // "v7-" + "9"  (Int tag boxed to String via +)
    }

    @TestAttribute
    fun inlineHygienicFreeCapture() {
        // #126: the caller's captured `x`=100 must NOT be shadowed by the inner crossinline host param `x`=7.
        assertEquals(107, hyg126Caller())          // 100 + 7 = 107  (unhygienic rebind would give 14)
        assertEquals(106, hyg126CallerChained())   // (100 + 1) + 5 = 106  (D2: value-capture rides through two hosts)
    }

    @TestAttribute
    fun genericParameterNullIdentity() {
        val tail = GenericNullLink(null)
        val head = GenericNullLink(tail)
        assertEquals(tail, genericNullTail(head))
    }
}
