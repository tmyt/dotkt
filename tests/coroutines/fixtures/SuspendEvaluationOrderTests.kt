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
// The interleaved-order case captures side effects into `suspendEvaluationEoLog` (asserted positionally, strictly stronger than
// the old stdout order-diff). Top-level names are family-prefixed (`suspendEvaluationEo`/`suspendEvaluationFo`/`suspendEvaluationAo`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-coevalorder: `side() + g()` must evaluate the left side effect BEFORE g() runs ----------------------
val suspendEvaluationEoLog = mutableListOf<String>()
fun suspendEvaluationEoSide(): Int { suspendEvaluationEoLog.add("L"); return 1 }
suspend fun suspendEvaluationEoG(): Int { suspendEvaluationEoLog.add("G"); return 2 }
suspend fun suspendEvaluationEoF(): Int {
    val r = suspendEvaluationEoSide() + suspendEvaluationEoG()   // strict left-to-right: L then G, sum = 3
    return r
}

// ---- il-cofieldorder: a raw @ClrField read left of a mutating suspend must read the PRE-mutation value --------
// NB `ClrField` is deliberately UNPREFIXED: kotc recognizes the @ClrField opt-in by SHORT NAME to make `x` a plain
// CLR field (a raw `field` read, no getter) — exactly the shape the N4 fix targets. It is the only ClrField in this
// assembly, so there is no collision. (This is the standalone annotation the old cases/il-cofieldorder declared.)
annotation class ClrField
suspend fun suspendEvaluationFoRelay(): Int = 5             // a suspend call that completes synchronously
class SuspendEvaluationFoBox {
    @ClrField var x: Int = 10                  // plain CLR field -> a raw `field` read (no property getter)
    suspend fun bump(): Int { x = 100; return suspendEvaluationFoRelay() }   // MUTATES x, then suspends
    suspend fun compute(): Int = x + bump()    // x read LEFT of the suspending bump() -> must be 10, not 100
}

// ---- il-coarrayorder: an arrayGet left of a mutating suspend must read the PRE-mutation element ---------------
suspend fun suspendEvaluationAoRelay(): Int = 5
suspend fun suspendEvaluationAoBump(a: IntArray): Int { a[0] = 100; return suspendEvaluationAoRelay() }   // MUTATES a[0], then suspends
suspend fun suspendEvaluationAoCompute(): Int {
    val a = intArrayOf(10, 20, 30)
    return a[0] + suspendEvaluationAoBump(a)                // a[0] read LEFT of the suspending bump(a) -> must be 10, not 100
}

// ---- a cross-module data-class `copy` whose omitted fields are reconstructed from a SUSPENDING receiver -------
// kotlin.Triple comes from the stdlib, so kotc cannot read `copy`'s default value and reconstructs each omitted field
// as a read of the call's receiver. The receiver must be bound to ONE single-evaluation temp — and when a later
// argument suspends, that temp lives across the suspension, so the reconstructed read must also be stamped with the
// INSTANTIATED field type (an open positional type variable is unresolvable in the state machine's own frame:
// bir2cir spilled it into an SM field of that type and the first resume threw InvalidProgramException).
val suspendEvaluationCpLog = mutableListOf<String>()
suspend fun suspendEvaluationCpTriple(): Triple<Int, Int, Int> { suspendEvaluationCpLog.add("T"); return Triple(1, 2, 3) }
suspend fun suspendEvaluationCpArg(): Int { suspendEvaluationCpLog.add("A"); return 9 }
suspend fun suspendEvaluationCpOmitOnly(): String = suspendEvaluationCpTriple().copy(second = 9).toString()
suspend fun suspendEvaluationCpSuspendingArg(): String = suspendEvaluationCpTriple().copy(second = suspendEvaluationCpArg()).toString()

// ---- an operand whose DESUGAR carries no type stamp, spilled left of a suspension ---------------------------
// `h(x!!, susp())`: kotc lowers `x!!` to `{ var __nn = x; if (__nn != null) __nn else throw NPE }` — a valueBlock
// and a cond, neither stamped with a type, over a `local` read of the temp the block itself declares. The operand
// is impure, so it spills into an SM field ahead of the suspending argument, and a spill field must be TYPED.
// Until the shared node-local deriver read a cond through its LIVE branch (a `throw` arm says nothing about the
// value's type) and a `local` through the block's own `var`, nothing could type these and the whole function was
// REJECTED at compile time — an abort on frontend-accepted source. Four desugars reach the same stamp-less shape
// and each answers through a different slot: the reference `!!` (the block's `var`), the value-type `!!` and the
// elvis (`nullableValue.elem`), and a bare-receiver safe call (a raw `cond` whose live arm is a `nullableWrap`).
val suspendEvaluationUtLog = mutableListOf<String>()
suspend fun suspendEvaluationUtSusp(): Int { suspendEvaluationUtLog.add("S"); return 2 }
fun suspendEvaluationUtHs(a: String, b: Int): String = "$a/$b"
fun suspendEvaluationUtHi(a: Int, b: Int): String = "$a/$b"
fun suspendEvaluationUtHq(a: Int?, b: Int): String = "$a/$b"

class SuspendEvaluationUtBox(val name: String?) { fun size(): Int = 7 }

fun suspendEvaluationUtSideRef(): String? { suspendEvaluationUtLog.add("L"); return "v" }
fun suspendEvaluationUtSideVal(): Int? { suspendEvaluationUtLog.add("L"); return 7 }
fun suspendEvaluationUtSideBox(): SuspendEvaluationUtBox { suspendEvaluationUtLog.add("L"); return SuspendEvaluationUtBox("n") }

suspend fun suspendEvaluationUtRefBang(): String = suspendEvaluationUtHs(suspendEvaluationUtSideRef()!!, suspendEvaluationUtSusp())
suspend fun suspendEvaluationUtValueBang(): String = suspendEvaluationUtHi(suspendEvaluationUtSideVal()!!, suspendEvaluationUtSusp())
suspend fun suspendEvaluationUtFieldBang(): String = suspendEvaluationUtHs(suspendEvaluationUtSideBox().name!!, suspendEvaluationUtSusp())
suspend fun suspendEvaluationUtElvis(): String = suspendEvaluationUtHi(suspendEvaluationUtSideVal() ?: 0, suspendEvaluationUtSusp())
suspend fun suspendEvaluationUtSafeCall(b: SuspendEvaluationUtBox?): String = suspendEvaluationUtHq(b?.size(), suspendEvaluationUtSusp())

class SuspendEvaluationOrderTests {
    @TestAttribute
    fun sideEffectBeforeSuspend() {
        suspendEvaluationEoLog.clear()
        val v = blockOn { suspendEvaluationEoF() }
        assertEquals(3, v)                  // 3
        assertEquals(2, suspendEvaluationEoLog.size)
        assertEquals("L", suspendEvaluationEoLog[0])     // former golden line 1
        assertEquals("G", suspendEvaluationEoLog[1])     // former golden line 2
    }

    @TestAttribute
    fun rawFieldReadBeforeSuspend() {
        val b = SuspendEvaluationFoBox()
        assertEquals(15, blockOn { b.compute() })   // 10 + 5 = 15 (a miscompile prints 105)
        assertEquals(100, b.x)                       // bump() did run and mutate the field
    }

    @TestAttribute
    fun arrayElemReadBeforeSuspend() {
        assertEquals(15, blockOn { suspendEvaluationAoCompute() })   // 10 + 5 = 15 (a miscompile prints 105)
    }

    // A cross-module data-class `copy` with an omitted field, on a SUSPENDING receiver.
    @TestAttribute
    fun crossModuleCopyReceiverSuspends() {
        suspendEvaluationCpLog.clear()
        assertEquals("(1, 9, 3)", blockOn { suspendEvaluationCpOmitOnly() })
        assertEquals(1, suspendEvaluationCpLog.size)                 // the suspending receiver ran ONCE (was 3)
        assertEquals("T", suspendEvaluationCpLog[0])

        // The provided argument suspends too, so the receiver temp must survive that suspension.
        suspendEvaluationCpLog.clear()
        assertEquals("(1, 9, 3)", blockOn { suspendEvaluationCpSuspendingArg() })
        assertEquals(2, suspendEvaluationCpLog.size)
        assertEquals("T", suspendEvaluationCpLog[0])                 // receiver first
        assertEquals("A", suspendEvaluationCpLog[1])                 // then the argument (was "T","T","A","T")
    }

    // Each of these was a COMPILE ABORT ("the operand ... carries no static type") before the desugars' own type
    // slots were read; the assertions additionally pin the left-to-right order the spill exists to preserve.
    @TestAttribute
    fun notNullAssertedReferenceBeforeSuspension() {
        suspendEvaluationUtLog.clear()
        assertEquals("v/2", blockOn { suspendEvaluationUtRefBang() })
        assertEquals(2, suspendEvaluationUtLog.size)
        assertEquals("L", suspendEvaluationUtLog[0])                 // the `!!` operand ran BEFORE the suspending argument
        assertEquals("S", suspendEvaluationUtLog[1])
    }

    @TestAttribute
    fun notNullAssertedValueTypeBeforeSuspension() {
        suspendEvaluationUtLog.clear()
        assertEquals("7/2", blockOn { suspendEvaluationUtValueBang() })   // `Nullable<Int>.Value`, not a boxed slot
        assertEquals(2, suspendEvaluationUtLog.size)
        assertEquals("L", suspendEvaluationUtLog[0])
    }

    @TestAttribute
    fun notNullAssertedFieldReadBeforeSuspension() {
        suspendEvaluationUtLog.clear()
        assertEquals("n/2", blockOn { suspendEvaluationUtFieldBang() })
        assertEquals(2, suspendEvaluationUtLog.size)
        assertEquals("L", suspendEvaluationUtLog[0])
    }

    @TestAttribute
    fun elvisOperandBeforeSuspension() {
        suspendEvaluationUtLog.clear()
        assertEquals("7/2", blockOn { suspendEvaluationUtElvis() })
        assertEquals(2, suspendEvaluationUtLog.size)
        assertEquals("L", suspendEvaluationUtLog[0])
    }

    @TestAttribute
    fun safeCallOperandBeforeSuspension() {
        // A bare-local receiver, so the safe call is a RAW `cond` with no `type` stamp at all.
        assertEquals("7/2", blockOn { suspendEvaluationUtSafeCall(SuspendEvaluationUtBox("q")) })
        assertEquals("null/2", blockOn { suspendEvaluationUtSafeCall(null) })
    }
}
