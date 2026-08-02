// Left-to-right eval-order-across-a-suspension battery (feature fixture). These lock the bir2cir SuspendColdLowering
// rule that an impure LEFT operand of `left OP g()` (where g() suspends) is spilled into an SM temp BEFORE the
// suspension, so it is observed at its lexical position, not after g()'s resume. Each old `suspend fun main` +
// stdout golden becomes one @TestAttribute method driven by the shared `dotkt.support.blockOn` cold-core harness
// (relay/g complete synchronously, so the reorder is observable purely through the interleaved side effect).
//
// Coverage preserved (old case -> method):
//   il-coevalorder  -> coEvalOrder_sideEffectBeforeSuspend   (BUG 2: `side() + g()` must observe L then G)
//   il-cofieldorder -> coFieldOrder_rawFieldReadBeforeSuspend(N4: a raw @ClrField read left of a mutating suspend)
//   il-coarrayorder -> coArrayOrder_arrayElemReadBeforeSuspend(N4-sibling: an arrayGet left of a mutating suspend)
//
// The interleaved-order case captures side effects into `suspendEvaluationSideEffectLog` (asserted positionally, strictly stronger than
// the old stdout order-diff). Top-level names are family-prefixed (`suspendEvaluationSideEffect`/`suspendEvaluationFieldOrder`/`suspendEvaluationArrayOrder`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-coevalorder: `side() + g()` must evaluate the left side effect BEFORE g() runs ----------------------
val suspendEvaluationSideEffectLog = mutableListOf<String>()
fun suspendEvaluationSideEffectSide(): Int { suspendEvaluationSideEffectLog.add("L"); return 1 }
suspend fun suspendEvaluationSideEffectG(): Int { suspendEvaluationSideEffectLog.add("G"); return 2 }
suspend fun suspendEvaluationSideEffectF(): Int {
    val r = suspendEvaluationSideEffectSide() + suspendEvaluationSideEffectG()   // strict left-to-right: L then G, sum = 3
    return r
}

// ---- il-cofieldorder: a raw @ClrField read left of a mutating suspend must read the PRE-mutation value --------
// NB `ClrField` is deliberately UNPREFIXED: kotc recognizes the @ClrField opt-in by SHORT NAME to make `x` a plain
// CLR field (a raw `field` read, no getter) — exactly the shape the N4 fix targets. It is the only ClrField in this
// assembly, so there is no collision. (This is the standalone annotation the old cases/il-cofieldorder declared.)
annotation class ClrField
suspend fun suspendEvaluationFieldOrderRelay(): Int = 5             // a suspend call that completes synchronously
class SuspendEvaluationFieldOrderBox {
    @ClrField var x: Int = 10                  // plain CLR field -> a raw `field` read (no property getter)
    suspend fun bump(): Int { x = 100; return suspendEvaluationFieldOrderRelay() }   // MUTATES x, then suspends
    suspend fun compute(): Int = x + bump()    // x read LEFT of the suspending bump() -> must be 10, not 100
}

// ---- il-coarrayorder: an arrayGet left of a mutating suspend must read the PRE-mutation element ---------------
suspend fun suspendEvaluationArrayOrderRelay(): Int = 5
suspend fun suspendEvaluationArrayOrderBump(a: IntArray): Int { a[0] = 100; return suspendEvaluationArrayOrderRelay() }   // MUTATES a[0], then suspends
suspend fun suspendEvaluationArrayOrderCompute(): Int {
    val a = intArrayOf(10, 20, 30)
    return a[0] + suspendEvaluationArrayOrderBump(a)                // a[0] read LEFT of the suspending bump(a) -> must be 10, not 100
}

// ---- a cross-module data-class `copy` whose omitted fields are reconstructed from a SUSPENDING receiver -------
// kotlin.Triple comes from the stdlib, so kotc cannot read `copy`'s default value and reconstructs each omitted field
// as a read of the call's receiver. The receiver must be bound to ONE single-evaluation temp — and when a later
// argument suspends, that temp lives across the suspension, so the reconstructed read must also be stamped with the
// INSTANTIATED field type (an open positional type variable is unresolvable in the state machine's own frame:
// bir2cir spilled it into an SM field of that type and the first resume threw InvalidProgramException).
val suspendEvaluationCopyReceiverLog = mutableListOf<String>()
suspend fun suspendEvaluationCopyReceiverTriple(): Triple<Int, Int, Int> { suspendEvaluationCopyReceiverLog.add("T"); return Triple(1, 2, 3) }
suspend fun suspendEvaluationCopyReceiverArg(): Int { suspendEvaluationCopyReceiverLog.add("A"); return 9 }
suspend fun suspendEvaluationCopyReceiverOmitOnly(): String = suspendEvaluationCopyReceiverTriple().copy(second = 9).toString()
suspend fun suspendEvaluationCopyReceiverSuspendingArg(): String = suspendEvaluationCopyReceiverTriple().copy(second = suspendEvaluationCopyReceiverArg()).toString()

// ---- an operand whose DESUGAR carries no type stamp, spilled left of a suspension ---------------------------
// `h(x!!, susp())`: kotc lowers `x!!` to `{ var __nn = x; if (__nn != null) __nn else throw NPE }` — a valueBlock
// and a cond, neither stamped with a type, over a `local` read of the temp the block itself declares. The operand
// is impure, so it spills into an SM field ahead of the suspending argument, and a spill field must be TYPED.
// Until the shared node-local deriver read a cond through its LIVE branch (a `throw` arm says nothing about the
// value's type) and a `local` through the block's own `var`, nothing could type these and the whole function was
// REJECTED at compile time — an abort on frontend-accepted source. Four desugars reach the same stamp-less shape
// and each answers through a different slot: the reference `!!` (the block's `var`), the value-type `!!` and the
// elvis (`nullableValue.elem`), and a bare-receiver safe call (a raw `cond` whose live arm is a `nullableWrap`).
val suspendEvaluationUntypedOperandLog = mutableListOf<String>()
suspend fun suspendEvaluationUntypedOperandSusp(): Int { suspendEvaluationUntypedOperandLog.add("S"); return 2 }
fun suspendEvaluationUntypedOperandHs(a: String, b: Int): String = "$a/$b"
fun suspendEvaluationUntypedOperandHi(a: Int, b: Int): String = "$a/$b"
fun suspendEvaluationUntypedOperandHq(a: Int?, b: Int): String = "$a/$b"

class SuspendEvaluationUntypedOperandBox(val name: String?) { fun size(): Int = 7 }

fun suspendEvaluationUntypedOperandSideRef(): String? { suspendEvaluationUntypedOperandLog.add("L"); return "v" }
fun suspendEvaluationUntypedOperandSideVal(): Int? { suspendEvaluationUntypedOperandLog.add("L"); return 7 }
fun suspendEvaluationUntypedOperandSideBox(): SuspendEvaluationUntypedOperandBox { suspendEvaluationUntypedOperandLog.add("L"); return SuspendEvaluationUntypedOperandBox("n") }

suspend fun suspendEvaluationUntypedOperandRefBang(): String = suspendEvaluationUntypedOperandHs(suspendEvaluationUntypedOperandSideRef()!!, suspendEvaluationUntypedOperandSusp())
suspend fun suspendEvaluationUntypedOperandValueBang(): String = suspendEvaluationUntypedOperandHi(suspendEvaluationUntypedOperandSideVal()!!, suspendEvaluationUntypedOperandSusp())
suspend fun suspendEvaluationUntypedOperandFieldBang(): String = suspendEvaluationUntypedOperandHs(suspendEvaluationUntypedOperandSideBox().name!!, suspendEvaluationUntypedOperandSusp())
suspend fun suspendEvaluationUntypedOperandElvis(): String = suspendEvaluationUntypedOperandHi(suspendEvaluationUntypedOperandSideVal() ?: 0, suspendEvaluationUntypedOperandSusp())
suspend fun suspendEvaluationUntypedOperandSafeCall(b: SuspendEvaluationUntypedOperandBox?): String = suspendEvaluationUntypedOperandHq(b?.size(), suspendEvaluationUntypedOperandSusp())

class SuspendEvaluationOrderTests {
    @TestAttribute
    fun sideEffectBeforeSuspend() {
        suspendEvaluationSideEffectLog.clear()
        val v = blockOn { suspendEvaluationSideEffectF() }
        assertEquals(3, v)                  // 3
        assertEquals(2, suspendEvaluationSideEffectLog.size)
        assertEquals("L", suspendEvaluationSideEffectLog[0])     // former golden line 1
        assertEquals("G", suspendEvaluationSideEffectLog[1])     // former golden line 2
    }

    @TestAttribute
    fun rawFieldReadBeforeSuspend() {
        val b = SuspendEvaluationFieldOrderBox()
        assertEquals(15, blockOn { b.compute() })   // 10 + 5 = 15 (a miscompile prints 105)
        assertEquals(100, b.x)                       // bump() did run and mutate the field
    }

    @TestAttribute
    fun arrayElemReadBeforeSuspend() {
        assertEquals(15, blockOn { suspendEvaluationArrayOrderCompute() })   // 10 + 5 = 15 (a miscompile prints 105)
    }

    // A cross-module data-class `copy` with an omitted field, on a SUSPENDING receiver.
    @TestAttribute
    fun crossModuleCopyReceiverSuspends() {
        suspendEvaluationCopyReceiverLog.clear()
        assertEquals("(1, 9, 3)", blockOn { suspendEvaluationCopyReceiverOmitOnly() })
        assertEquals(1, suspendEvaluationCopyReceiverLog.size)                 // the suspending receiver ran ONCE (was 3)
        assertEquals("T", suspendEvaluationCopyReceiverLog[0])

        // The provided argument suspends too, so the receiver temp must survive that suspension.
        suspendEvaluationCopyReceiverLog.clear()
        assertEquals("(1, 9, 3)", blockOn { suspendEvaluationCopyReceiverSuspendingArg() })
        assertEquals(2, suspendEvaluationCopyReceiverLog.size)
        assertEquals("T", suspendEvaluationCopyReceiverLog[0])                 // receiver first
        assertEquals("A", suspendEvaluationCopyReceiverLog[1])                 // then the argument (was "T","T","A","T")
    }

    // Each of these was a COMPILE ABORT ("the operand ... carries no static type") before the desugars' own type
    // slots were read; the assertions additionally pin the left-to-right order the spill exists to preserve.
    @TestAttribute
    fun notNullAssertedReferenceBeforeSuspension() {
        suspendEvaluationUntypedOperandLog.clear()
        assertEquals("v/2", blockOn { suspendEvaluationUntypedOperandRefBang() })
        assertEquals(2, suspendEvaluationUntypedOperandLog.size)
        assertEquals("L", suspendEvaluationUntypedOperandLog[0])                 // the `!!` operand ran BEFORE the suspending argument
        assertEquals("S", suspendEvaluationUntypedOperandLog[1])
    }

    @TestAttribute
    fun notNullAssertedValueTypeBeforeSuspension() {
        suspendEvaluationUntypedOperandLog.clear()
        assertEquals("7/2", blockOn { suspendEvaluationUntypedOperandValueBang() })   // `Nullable<Int>.Value`, not a boxed slot
        assertEquals(2, suspendEvaluationUntypedOperandLog.size)
        assertEquals("L", suspendEvaluationUntypedOperandLog[0])
    }

    @TestAttribute
    fun notNullAssertedFieldReadBeforeSuspension() {
        suspendEvaluationUntypedOperandLog.clear()
        assertEquals("n/2", blockOn { suspendEvaluationUntypedOperandFieldBang() })
        assertEquals(2, suspendEvaluationUntypedOperandLog.size)
        assertEquals("L", suspendEvaluationUntypedOperandLog[0])
    }

    @TestAttribute
    fun elvisOperandBeforeSuspension() {
        suspendEvaluationUntypedOperandLog.clear()
        assertEquals("7/2", blockOn { suspendEvaluationUntypedOperandElvis() })
        assertEquals(2, suspendEvaluationUntypedOperandLog.size)
        assertEquals("L", suspendEvaluationUntypedOperandLog[0])
    }

    @TestAttribute
    fun safeCallOperandBeforeSuspension() {
        // A bare-local receiver, so the safe call is a RAW `cond` with no `type` stamp at all.
        assertEquals("7/2", blockOn { suspendEvaluationUntypedOperandSafeCall(SuspendEvaluationUntypedOperandBox("q")) })
        assertEquals("null/2", blockOn { suspendEvaluationUntypedOperandSafeCall(null) })
    }
}
