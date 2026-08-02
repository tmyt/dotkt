// The INLINE call's evaluation plan, under suspension — where the semantic layer (docs/bir-cir-spec.md §2.7) meets
// the physical one (docs/dotkt-semantics.md §7.1). A plan binding is a request for a scoped local; whether the state
// machine can keep it in the frame or must promote it to an instance field is decided ~300 passes later from
// liveness, and that decision needs a TYPE for every slot it mints.
//
// What these pin:
//   * an inline call's bound values survive a suspension inside the spliced body — one evaluation, right order;
//   * a filled default still runs after every supplied value when the call also suspends;
//   * a SPLICED INLINE CALL used as an operand to the left of a suspending one is spilled into a typed slot. That
//     spill used to fall back to `kotlin.Any` behind a warning, because the splice minted its block without a type
//     stamp: a value type reaching that slot is a boxed field the CLR reads back wrong. The block now carries the
//     callee's closed return type, and an untyped spill is a hard error naming the lowering that dropped it.
//
// The relays complete synchronously — the reorder/re-evaluation faults these lock are observable purely through the
// interleaved side effects, exactly as in SuspendEvaluationOrderTests. Top-level names carry the `inlineSuspendPlan` prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

val inlineSuspendPlanLog = mutableListOf<String>()

fun inlineSuspendPlanT(tag: String): Int { inlineSuspendPlanLog.add(tag); return tag.length }
suspend fun inlineSuspendPlanRelay(tag: String): Int { inlineSuspendPlanLog.add(tag); return tag.length }

private fun inlineSuspendPlanTrace(): String = inlineSuspendPlanLog.joinToString(",")

// ---- a bound value read AFTER a suspension inside the spliced body -------------------------------------------
inline fun inlineSuspendPlanAround(x: Int, block: () -> Int): Int = block() + x + x

// ---- a filled default, on a call whose lambda suspends --------------------------------------------------------
inline fun inlineSuspendPlanWithDefault(a: Int = inlineSuspendPlanT("A"), b: Int, block: (Int) -> Int): Int = block(a) + b

// ---- a spliced inline call as the LEFT operand of a suspending one -------------------------------------------
fun inlineSuspendPlanPair(a: Int, b: Int): String = "$a/$b"
fun inlineSuspendPlanJoin(a: String, b: Int): String = "$a|$b"

suspend fun inlineSuspendPlanSplicedLeftOfSuspend(x: Int): String =
    inlineSuspendPlanPair(x.let { it + 1 }, inlineSuspendPlanRelay("S"))

// The same shape with a REFERENCE result, so the two halves of the type stamp (value type / reference type) are
// both exercised through the spill.
suspend fun inlineSuspendPlanSplicedLeftOfSuspendRef(x: Int): String =
    inlineSuspendPlanJoin(x.let { "v$it" }, inlineSuspendPlanRelay("S"))

// ---- a generic inline call whose bound value lives across the suspension ---------------------------------------
inline fun <T> inlineSuspendPlanHold(v: T, block: () -> Unit): T { block(); return v }

// ---- a TERMINAL operand left of a suspending one ---------------------------------------------------------------
// An operand that never completes — an expression-position `throw`/`return`, a `Nothing`-returning call, or the
// block a lambda-only inline call splices around one — makes the whole expression unreachable: Kotlin evaluates it
// and NOTHING to its right, including the suspension. It is therefore not a value to spill across a resume; it IS
// the expression's value. (`run { … }` supplies no value, so it carries no plan and its splice is an untyped block —
// which is how the spill came to ask for a type that no longer existed and refuse.)
suspend fun inlineSuspendPlanTerminalThrow(): Int =
    inlineSuspendPlanSum(run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("boom") }, inlineSuspendPlanRelay("S"))

suspend fun inlineSuspendPlanTerminalReturn(): Int {
    return inlineSuspendPlanSum(run<Int> { inlineSuspendPlanLog.add("R"); return -7 }, inlineSuspendPlanRelay("S"))
}

fun inlineSuspendPlanNever(): Nothing { inlineSuspendPlanLog.add("N"); throw IllegalArgumentException("nope") }
suspend fun inlineSuspendPlanTerminalNothingCall(): Int = inlineSuspendPlanSum(inlineSuspendPlanNever(), inlineSuspendPlanRelay("S"))

// …and the mirror: a terminal operand to the RIGHT of the suspension, where the suspension DOES run first.
suspend fun inlineSuspendPlanTerminalAfterSuspend(): Int =
    inlineSuspendPlanSum(inlineSuspendPlanRelay("S"), run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("late") })

// A `Nothing`-typed LOCAL is never live across anything — its initializer is what does not complete — so the
// storage question never arises for it. This pins that, since the local read is an operand like any other.
suspend fun inlineSuspendPlanNothingLocal(): Int {
    val x = run<Int> { inlineSuspendPlanLog.add("X"); throw IllegalStateException("local") }
    return inlineSuspendPlanSum(x, inlineSuspendPlanRelay("S"))
}

fun inlineSuspendPlanSum(a: Int, b: Int): Int = a + b

// …and the same operand in the argument list of a SUSPENDING call, where the machinery it interacts with is the
// suspension point itself rather than the evaluation-order spill. With no suspension to its RIGHT nothing has to be
// elided, so these lower exactly as they always did: everything to its left evaluates — a suspension there completes
// and resumes normally — then the operand leaves, and the call it was an argument to never runs.
suspend fun inlineSuspendPlanSuspSum(a: Int, b: Int): Int { inlineSuspendPlanLog.add("O"); return a + b }
suspend fun inlineSuspendPlanSuspOne(a: Int): Int { inlineSuspendPlanLog.add("O"); return a }

suspend fun inlineSuspendPlanSuspTerminalOnly(): Int =
    inlineSuspendPlanSuspOne(run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("one") })

suspend fun inlineSuspendPlanSuspAfterSuspension(): Int =
    inlineSuspendPlanSuspSum(inlineSuspendPlanRelay("S"), run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("two") })

// The suspend-function-VALUE forms of both: a different cold-call builder, the same rule.
val inlineSuspendPlanFnOne: suspend (Int) -> Int = { a -> inlineSuspendPlanLog.add("V"); a }
val inlineSuspendPlanFnTwo: suspend (Int, Int) -> Int = { a, b -> inlineSuspendPlanLog.add("V"); a + b }

suspend fun inlineSuspendPlanFnValueTerminalOnly(): Int =
    inlineSuspendPlanFnOne(run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("fnOne") })

suspend fun inlineSuspendPlanFnValueAfterSuspension(): Int =
    inlineSuspendPlanFnTwo(inlineSuspendPlanRelay("S"), run<Int> { inlineSuspendPlanLog.add("T"); throw IllegalStateException("fnTwo") })

private inline fun inlineSuspendPlanThrown(block: () -> Unit): String =
    try { block(); "no-throw" } catch (e: Throwable) { e.message ?: "?" }

class InlineEvaluationPlanSuspendTests {
    /** The bound value is evaluated once, before the body, and survives a suspension the body performs. */
    @TestAttribute
    fun boundValueSurvivesASuspensionInTheBody() {
        inlineSuspendPlanLog.clear()
        val v = blockOn { inlineSuspendPlanAround(inlineSuspendPlanT("xyz")) { inlineSuspendPlanRelay("R") } }
        assertEquals(7, v)                        // R=1 + 3 + 3
        assertEquals("xyz,R", inlineSuspendPlanTrace())      // the argument once, before the body
    }

    /** A filled default still follows every supplied value when the call suspends. */
    @TestAttribute
    fun filledDefaultFollowsSuppliedValueUnderSuspension() {
        inlineSuspendPlanLog.clear()
        val v = blockOn { inlineSuspendPlanWithDefault(b = inlineSuspendPlanT("BB")) { it + inlineSuspendPlanRelay("R") } }
        assertEquals(4, v)                        // block(1) = 1+1 = 2, + 2
        assertEquals("BB,A,R", inlineSuspendPlanTrace())
    }

    /** A spliced inline call to the LEFT of a suspending operand is spilled into a TYPED slot. */
    @TestAttribute
    fun splicedInlineCallSpilledLeftOfASuspension() {
        inlineSuspendPlanLog.clear()
        assertEquals("4/1", blockOn { inlineSuspendPlanSplicedLeftOfSuspend(3) })
        assertEquals("S", inlineSuspendPlanTrace())

        inlineSuspendPlanLog.clear()
        assertEquals("v3|1", blockOn { inlineSuspendPlanSplicedLeftOfSuspendRef(3) })
        assertEquals("S", inlineSuspendPlanTrace())
    }

    /** A generic inline fn's bound value, held across a suspension the body performs. */
    @TestAttribute
    fun genericBoundValueHeldAcrossASuspension() {
        inlineSuspendPlanLog.clear()
        assertEquals(3, blockOn { inlineSuspendPlanHold(inlineSuspendPlanT("abc")) { inlineSuspendPlanRelay("R") } })
        assertEquals("abc,R", inlineSuspendPlanTrace())

        inlineSuspendPlanLog.clear()
        assertEquals("kept", blockOn { inlineSuspendPlanHold("kept") { inlineSuspendPlanRelay("R") } })
        assertEquals("R", inlineSuspendPlanTrace())
    }

    /** An operand that never completes, left of a suspending one: it runs, and the suspension never does. */
    @TestAttribute
    fun terminalOperandLeftOfASuspension() {
        inlineSuspendPlanLog.clear()
        assertEquals("boom", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanTerminalThrow() } })
        assertEquals("T", inlineSuspendPlanTrace())              // the suspending operand was never reached

        inlineSuspendPlanLog.clear()
        assertEquals(-7, blockOn { inlineSuspendPlanTerminalReturn() })
        assertEquals("R", inlineSuspendPlanTrace())

        inlineSuspendPlanLog.clear()
        assertEquals("nope", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanTerminalNothingCall() } })
        assertEquals("N", inlineSuspendPlanTrace())
    }

    /** …and one to its RIGHT: the suspension runs first, then the operand leaves. */
    @TestAttribute
    fun terminalOperandRightOfASuspension() {
        inlineSuspendPlanLog.clear()
        assertEquals("late", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanTerminalAfterSuspend() } })
        assertEquals("S,T", inlineSuspendPlanTrace())
    }

    /** A `Nothing`-typed local: its initializer is what does not complete, so nothing is ever stored. */
    @TestAttribute
    fun nothingTypedLocalIsNeverStored() {
        inlineSuspendPlanLog.clear()
        assertEquals("local", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanNothingLocal() } })
        assertEquals("X", inlineSuspendPlanTrace())
    }

    /** A terminal operand in a SUSPENDING call's own argument list, with no suspension to its right: the operand
     *  leaves and the call never runs — through both the named-callee and the suspend-function-value builder. */
    @TestAttribute
    fun terminalOperandInASuspendingCallsArguments() {
        inlineSuspendPlanLog.clear()
        assertEquals("one", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanSuspTerminalOnly() } })
        assertEquals("T", inlineSuspendPlanTrace())              // the suspending callee was never entered

        inlineSuspendPlanLog.clear()
        assertEquals("fnOne", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanFnValueTerminalOnly() } })
        assertEquals("T", inlineSuspendPlanTrace())
    }

    /** …and with a suspension to its LEFT: that one completes and resumes, then the operand leaves. */
    @TestAttribute
    fun terminalOperandAfterASuspendingArgument() {
        inlineSuspendPlanLog.clear()
        assertEquals("two", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanSuspAfterSuspension() } })
        assertEquals("S,T", inlineSuspendPlanTrace())

        inlineSuspendPlanLog.clear()
        assertEquals("fnTwo", inlineSuspendPlanThrown { blockOn { inlineSuspendPlanFnValueAfterSuspension() } })
        assertEquals("S,T", inlineSuspendPlanTrace())
    }
}
