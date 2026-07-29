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
}
