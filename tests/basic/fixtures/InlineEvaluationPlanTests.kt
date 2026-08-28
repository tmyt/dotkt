// An INLINE call's arguments are call-site values: Kotlin evaluates each of them exactly once, at the call, in the
// order the call site writes them — and every value the call SUPPLIES before any default the callee fills. The
// callee's body is then free to read one of them N times, zero times, inside a loop or inside a closure without
// changing any of that. This battery locks all of it (docs/bir-cir-spec.md §2.7, granularity trigger (d)).
//
// The engine under test: kotc emits a call-evaluation plan for a `callInline`, bir2cir's InlineSplice consumes the
// bindings instead of minting a temp per parameter, and CallEvalLowering decides the physical form once. The
// regressions these pin, in the order the methods appear:
//   * order — binding a parameter per SLOT ran a filled default before a supplied argument sitting to its right;
//   * single evaluation — a body reading a parameter twice, or in a loop, must not re-run the argument;
//   * evaluated anyway — a body that never reads a parameter must still run its argument's side effect;
//   * nesting — an inline call inside an inline lambda resolves its own plan outward through the enclosing one;
//   * receivers — a dispatch/extension receiver is evaluated before the arguments, once;
//   * capture — a value the plan left as a read still reaches a closure the callee's body builds over it.
//
// Every side effect is captured into `iepLog` and asserted positionally, which is strictly stronger than a value
// assertion alone: a re-evaluated argument produces the right number twice and only the log shows it.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

val iepLog = mutableListOf<String>()

/** Records its tag and returns the tag's length — a value with an OBSERVABLE evaluation. */
fun iepT(tag: String): Int { iepLog.add(tag); return tag.length }

private fun iepTrace(): String = iepLog.joinToString(",")

// ---- order: supplied arguments run before filled defaults, whatever slots they sit in -------------------------
inline fun iepDefaults(a: Int = iepT("A"), b: Int, c: Int = iepT("C"), block: (Int) -> Int): Int =
    block(a) + b + c

/** …and a filled default may READ an earlier parameter, which by then holds the value the call bound. */
inline fun iepChained(a: Int, b: Int = iepT("b") + a, c: Int = iepT("c") + b, block: (Int) -> Int): Int =
    block(a) + b + c

// ---- single evaluation: the body reads the value twice, then inside a loop --------------------------------
inline fun iepReadTwice(x: Int, block: () -> Unit): Int { block(); return x + x }
inline fun iepReadInLoop(x: Int, n: Int, block: () -> Unit): Int {
    var sum = 0
    for (i in 0 until n) sum += x
    block()
    return sum
}

// ---- a parameter the body never reads is still an evaluated call-site value ------------------------------
@Suppress("UNUSED_PARAMETER")
inline fun iepIgnores(x: Int, block: () -> Int): Int = block()

// ---- nesting: an inline call inside an inline lambda, each with its own plan ------------------------------
inline fun iepInner(p: Int = iepT("i"), block: () -> Int): Int = p + block()
inline fun iepOuter(q: Int, block: () -> Int): Int = q + block()

// ---- forwarding: the enclosing inline fn's own lambda parameter passed BY NAME into a nested inline call ---
inline fun iepForwardTarget(v: Int, block: (Int) -> Int): Int = block(v) + block(v)
inline fun iepForwardSource(v: Int, block: (Int) -> Int): Int = iepForwardTarget(v, block)
inline fun iepOrdinarySelfName(__self: Int, block: (Int) -> Int): Int = block(__self) + __self

// ---- receivers: dispatch before arguments, extension before arguments, each once --------------------------
class IepBox(val n: Int) {
    inline fun scaled(k: Int, block: (Int) -> Int): Int = block(n * k) + n
}
fun iepBox(tag: String): IepBox { iepLog.add(tag); return IepBox(10) }

inline fun Int.iepShifted(by: Int, block: (Int) -> Int): Int = block(this + by) + this

// ---- generic + value-type instantiation: the bound value's type must survive into the spliced block -------
inline fun <T, R> iepMap(v: T, f: (T) -> R): R = f(v)

// ---- capture: the callee's body builds a CLOSURE over a parameter (a capture names a slot, not an expression)
inline fun iepAdder(base: Int, block: () -> Unit): (Int) -> Int { block(); return { it + base } }

class InlineEvaluationPlanTests {
    /** A supplied argument runs before a filled default even when the default's parameter sits to its LEFT. */
    @TestAttribute
    fun suppliedArgumentsRunBeforeFilledDefaults() {
        iepLog.clear()
        val r = iepDefaults(b = iepT("B")) { it * 2 }
        assertEquals(4, r)                    // block(1) + 1 + 1 = 2 + 1 + 1
        assertEquals("B,A,C", iepTrace())     // was "A,B,C": the fill ran in its own slot, ahead of the argument
    }

    /** Two filled defaults, the later one READING the earlier: declaration order, and each evaluated once. */
    @TestAttribute
    fun filledDefaultsRunInDeclarationOrderAndSeeEachOther() {
        iepLog.clear()
        val r = iepChained(iepT("A")) { it }
        assertEquals(6, r)                    // a=1, b=1+1=2, c=1+2=3 -> 1+2+3
        assertEquals("A,b,c", iepTrace())
    }

    /** A body that reads the value twice re-reads a LOCAL, never the argument expression. */
    @TestAttribute
    fun valueReadTwiceIsEvaluatedOnce() {
        iepLog.clear()
        assertEquals(6, iepReadTwice(iepT("xyz")) { iepLog.add("b") })
        assertEquals("xyz,b", iepTrace())
    }

    /** …and a body that reads it once per iteration evaluates the argument once, not once per iteration. */
    @TestAttribute
    fun valueReadInALoopIsEvaluatedOnce() {
        iepLog.clear()
        assertEquals(12, iepReadInLoop(iepT("abcd"), 3) { iepLog.add("b") })
        assertEquals("abcd,b", iepTrace())
    }

    /** Kotlin evaluates every value a call supplies, whether the callee reads it or not. */
    @TestAttribute
    fun unreadValueIsStillEvaluated() {
        iepLog.clear()
        assertEquals(5, iepIgnores(iepT("dropped")) { 5 })
        assertEquals("dropped", iepTrace())
    }

    /** An inline call inside an inline lambda: the inner plan lowers first and the outer's reads survive it. */
    @TestAttribute
    fun nestedInlineCallsKeepBothPlans() {
        iepLog.clear()
        val r = iepOuter(iepT("q")) { iepInner { 5 } }
        assertEquals(7, r)                    // q=1 + (i=1 + 5)
        assertEquals("q,i", iepTrace())       // the outer argument, then the inner's filled default
    }

    /** A lambda parameter forwarded BY NAME into a nested inline call is a carrier, not a bound value — while the
     *  ordinary argument beside it is bound once even though the nested body invokes the carrier twice. */
    @TestAttribute
    fun forwardedLambdaKeepsItsCarrier() {
        iepLog.clear()
        val r = iepForwardSource(iepT("fw")) { iepLog.add("c"); it + 1 }
        assertEquals(6, r)                    // (2+1) + (2+1)
        assertEquals("fw,c,c", iepTrace())    // the value once, the carrier spliced at both invokes
    }

    /** A user spelling `__self` does not turn an ordinary inline parameter into an extension receiver. */
    @TestAttribute
    fun ordinarySelfNameIsNotAnInlineReceiver() {
        assertEquals(15, iepOrdinarySelfName(5) { it * 2 })
    }

    /** A member inline fn evaluates its dispatch receiver before its arguments, once. */
    @TestAttribute
    fun dispatchReceiverRunsBeforeArguments() {
        iepLog.clear()
        val r = iepBox("R").scaled(iepT("kk")) { it + 1 }
        assertEquals(31, r)                   // block(10*2)=21 + 10
        assertEquals("R,kk", iepTrace())
    }

    /** An extension inline fn evaluates its extension receiver before its arguments, once. */
    @TestAttribute
    fun extensionReceiverRunsBeforeArguments() {
        iepLog.clear()
        val r = iepT("recv").iepShifted(iepT("by")) { it * 2 }
        assertEquals(16, r)                   // block(4+2)=12 + 4
        assertEquals("recv,by", iepTrace())
    }

    /** A generic inline fn over a VALUE type: the binding carries the caller-instantiated type into the block. */
    @TestAttribute
    fun genericInlineOverAValueType() {
        iepLog.clear()
        assertEquals(21, iepMap(iepT("sev".repeat(2) + "x")) { it * 3 })   // "sevsevx".length = 7
        assertEquals(35, iepMap(7) { it * 5 })
        assertEquals("abc", iepMap(listOf("a", "b", "c")) { it.joinToString("") })
    }

    /** A closure the callee's body builds over a parameter still sees the value the call bound. */
    @TestAttribute
    fun parameterCapturedByAClosureInTheBody() {
        iepLog.clear()
        val add = iepAdder(iepT("base")) { iepLog.add("b") }
        assertEquals(14, add(10))
        assertEquals(24, add(20))             // the capture holds ONE value, read twice
        assertEquals("base,b", iepTrace())
    }
}
