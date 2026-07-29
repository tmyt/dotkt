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
// interleaved side effects, exactly as in SuspendEvaluationOrderTests. Top-level names carry the `corIep` prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

val corIepLog = mutableListOf<String>()

fun corIepT(tag: String): Int { corIepLog.add(tag); return tag.length }
suspend fun corIepRelay(tag: String): Int { corIepLog.add(tag); return tag.length }

private fun corIepTrace(): String = corIepLog.joinToString(",")

// ---- a bound value read AFTER a suspension inside the spliced body -------------------------------------------
inline fun corIepAround(x: Int, block: () -> Int): Int = block() + x + x

// ---- a filled default, on a call whose lambda suspends --------------------------------------------------------
inline fun corIepWithDefault(a: Int = corIepT("A"), b: Int, block: (Int) -> Int): Int = block(a) + b

// ---- a spliced inline call as the LEFT operand of a suspending one -------------------------------------------
fun corIepPair(a: Int, b: Int): String = "$a/$b"
fun corIepJoin(a: String, b: Int): String = "$a|$b"

suspend fun corIepSplicedLeftOfSuspend(x: Int): String =
    corIepPair(x.let { it + 1 }, corIepRelay("S"))

// The same shape with a REFERENCE result, so the two halves of the type stamp (value type / reference type) are
// both exercised through the spill.
suspend fun corIepSplicedLeftOfSuspendRef(x: Int): String =
    corIepJoin(x.let { "v$it" }, corIepRelay("S"))

// ---- a generic inline call whose bound value lives across the suspension ---------------------------------------
inline fun <T> corIepHold(v: T, block: () -> Unit): T { block(); return v }

// ---- a TERMINAL operand left of a suspending one ---------------------------------------------------------------
// An operand that never completes — an expression-position `throw`/`return`, a `Nothing`-returning call, or the
// block a lambda-only inline call splices around one — makes the whole expression unreachable: Kotlin evaluates it
// and NOTHING to its right, including the suspension. It is therefore not a value to spill across a resume; it IS
// the expression's value. (`run { … }` supplies no value, so it carries no plan and its splice is an untyped block —
// which is how the spill came to ask for a type that no longer existed and refuse.)
suspend fun corIepTerminalThrow(): Int =
    corIepSum(run<Int> { corIepLog.add("T"); throw IllegalStateException("boom") }, corIepRelay("S"))

suspend fun corIepTerminalReturn(): Int {
    return corIepSum(run<Int> { corIepLog.add("R"); return -7 }, corIepRelay("S"))
}

fun corIepNever(): Nothing { corIepLog.add("N"); throw IllegalArgumentException("nope") }
suspend fun corIepTerminalNothingCall(): Int = corIepSum(corIepNever(), corIepRelay("S"))

// …and the mirror: a terminal operand to the RIGHT of the suspension, where the suspension DOES run first.
suspend fun corIepTerminalAfterSuspend(): Int =
    corIepSum(corIepRelay("S"), run<Int> { corIepLog.add("T"); throw IllegalStateException("late") })

// A `Nothing`-typed LOCAL is never live across anything — its initializer is what does not complete — so the
// storage question never arises for it. This pins that, since the local read is an operand like any other.
suspend fun corIepNothingLocal(): Int {
    val x = run<Int> { corIepLog.add("X"); throw IllegalStateException("local") }
    return corIepSum(x, corIepRelay("S"))
}

fun corIepSum(a: Int, b: Int): Int = a + b

// …and the same operand in the argument list of a SUSPENDING call, where the machinery it interacts with is the
// suspension point itself rather than the evaluation-order spill. With no suspension to its RIGHT nothing has to be
// elided, so these lower exactly as they always did: everything to its left evaluates — a suspension there completes
// and resumes normally — then the operand leaves, and the call it was an argument to never runs.
suspend fun corIepSuspSum(a: Int, b: Int): Int { corIepLog.add("O"); return a + b }
suspend fun corIepSuspOne(a: Int): Int { corIepLog.add("O"); return a }

suspend fun corIepSuspTerminalOnly(): Int =
    corIepSuspOne(run<Int> { corIepLog.add("T"); throw IllegalStateException("one") })

suspend fun corIepSuspAfterSuspension(): Int =
    corIepSuspSum(corIepRelay("S"), run<Int> { corIepLog.add("T"); throw IllegalStateException("two") })

// The suspend-function-VALUE forms of both: a different cold-call builder, the same rule.
val corIepFnOne: suspend (Int) -> Int = { a -> corIepLog.add("V"); a }
val corIepFnTwo: suspend (Int, Int) -> Int = { a, b -> corIepLog.add("V"); a + b }

suspend fun corIepFnValueTerminalOnly(): Int =
    corIepFnOne(run<Int> { corIepLog.add("T"); throw IllegalStateException("fnOne") })

suspend fun corIepFnValueAfterSuspension(): Int =
    corIepFnTwo(corIepRelay("S"), run<Int> { corIepLog.add("T"); throw IllegalStateException("fnTwo") })

private inline fun corIepThrown(block: () -> Unit): String =
    try { block(); "no-throw" } catch (e: Throwable) { e.message ?: "?" }

class InlineEvaluationPlanSuspendTests {
    /** The bound value is evaluated once, before the body, and survives a suspension the body performs. */
    @TestAttribute
    fun boundValueSurvivesASuspensionInTheBody() {
        corIepLog.clear()
        val v = blockOn { corIepAround(corIepT("xyz")) { corIepRelay("R") } }
        assertEquals(7, v)                        // R=1 + 3 + 3
        assertEquals("xyz,R", corIepTrace())      // the argument once, before the body
    }

    /** A filled default still follows every supplied value when the call suspends. */
    @TestAttribute
    fun filledDefaultFollowsSuppliedValueUnderSuspension() {
        corIepLog.clear()
        val v = blockOn { corIepWithDefault(b = corIepT("BB")) { it + corIepRelay("R") } }
        assertEquals(4, v)                        // block(1) = 1+1 = 2, + 2
        assertEquals("BB,A,R", corIepTrace())
    }

    /** A spliced inline call to the LEFT of a suspending operand is spilled into a TYPED slot. */
    @TestAttribute
    fun splicedInlineCallSpilledLeftOfASuspension() {
        corIepLog.clear()
        assertEquals("4/1", blockOn { corIepSplicedLeftOfSuspend(3) })
        assertEquals("S", corIepTrace())

        corIepLog.clear()
        assertEquals("v3|1", blockOn { corIepSplicedLeftOfSuspendRef(3) })
        assertEquals("S", corIepTrace())
    }

    /** A generic inline fn's bound value, held across a suspension the body performs. */
    @TestAttribute
    fun genericBoundValueHeldAcrossASuspension() {
        corIepLog.clear()
        assertEquals(3, blockOn { corIepHold(corIepT("abc")) { corIepRelay("R") } })
        assertEquals("abc,R", corIepTrace())

        corIepLog.clear()
        assertEquals("kept", blockOn { corIepHold("kept") { corIepRelay("R") } })
        assertEquals("R", corIepTrace())
    }

    /** An operand that never completes, left of a suspending one: it runs, and the suspension never does. */
    @TestAttribute
    fun terminalOperandLeftOfASuspension() {
        corIepLog.clear()
        assertEquals("boom", corIepThrown { blockOn { corIepTerminalThrow() } })
        assertEquals("T", corIepTrace())              // the suspending operand was never reached

        corIepLog.clear()
        assertEquals(-7, blockOn { corIepTerminalReturn() })
        assertEquals("R", corIepTrace())

        corIepLog.clear()
        assertEquals("nope", corIepThrown { blockOn { corIepTerminalNothingCall() } })
        assertEquals("N", corIepTrace())
    }

    /** …and one to its RIGHT: the suspension runs first, then the operand leaves. */
    @TestAttribute
    fun terminalOperandRightOfASuspension() {
        corIepLog.clear()
        assertEquals("late", corIepThrown { blockOn { corIepTerminalAfterSuspend() } })
        assertEquals("S,T", corIepTrace())
    }

    /** A `Nothing`-typed local: its initializer is what does not complete, so nothing is ever stored. */
    @TestAttribute
    fun nothingTypedLocalIsNeverStored() {
        corIepLog.clear()
        assertEquals("local", corIepThrown { blockOn { corIepNothingLocal() } })
        assertEquals("X", corIepTrace())
    }

    /** A terminal operand in a SUSPENDING call's own argument list, with no suspension to its right: the operand
     *  leaves and the call never runs — through both the named-callee and the suspend-function-value builder. */
    @TestAttribute
    fun terminalOperandInASuspendingCallsArguments() {
        corIepLog.clear()
        assertEquals("one", corIepThrown { blockOn { corIepSuspTerminalOnly() } })
        assertEquals("T", corIepTrace())              // the suspending callee was never entered

        corIepLog.clear()
        assertEquals("fnOne", corIepThrown { blockOn { corIepFnValueTerminalOnly() } })
        assertEquals("T", corIepTrace())
    }

    /** …and with a suspension to its LEFT: that one completes and resumes, then the operand leaves. */
    @TestAttribute
    fun terminalOperandAfterASuspendingArgument() {
        corIepLog.clear()
        assertEquals("two", corIepThrown { blockOn { corIepSuspAfterSuspension() } })
        assertEquals("S,T", corIepTrace())

        corIepLog.clear()
        assertEquals("fnTwo", corIepThrown { blockOn { corIepFnValueAfterSuspension() } })
        assertEquals("S,T", corIepTrace())
    }
}
