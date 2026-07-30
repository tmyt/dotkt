// Left-to-right eval-order-across-a-suspension battery (CorA batch). These lock the bir2cir SuspendColdLowering
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
// The interleaved-order case captures side effects into `corAEoLog` (asserted positionally, strictly stronger than
// the old stdout order-diff). Top-level names are family-prefixed (`corAEo`/`corAFo`/`corAAo`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-coevalorder: `side() + g()` must evaluate the left side effect BEFORE g() runs ----------------------
val corAEoLog = mutableListOf<String>()
fun corAEoSide(): Int { corAEoLog.add("L"); return 1 }
suspend fun corAEoG(): Int { corAEoLog.add("G"); return 2 }
suspend fun corAEoF(): Int {
    val r = corAEoSide() + corAEoG()   // strict left-to-right: L then G, sum = 3
    return r
}

// ---- il-cofieldorder: a raw @ClrField read left of a mutating suspend must read the PRE-mutation value --------
// NB `ClrField` is deliberately UNPREFIXED: kotc recognizes the @ClrField opt-in by SHORT NAME to make `x` a plain
// CLR field (a raw `field` read, no getter) — exactly the shape the N4 fix targets. It is the only ClrField in this
// assembly, so there is no collision. (This is the standalone annotation the old cases/il-cofieldorder declared.)
annotation class ClrField
suspend fun corAFoRelay(): Int = 5             // a suspend call that completes synchronously
class CorAFoBox {
    @ClrField var x: Int = 10                  // plain CLR field -> a raw `field` read (no property getter)
    suspend fun bump(): Int { x = 100; return corAFoRelay() }   // MUTATES x, then suspends
    suspend fun compute(): Int = x + bump()    // x read LEFT of the suspending bump() -> must be 10, not 100
}

// ---- il-coarrayorder: an arrayGet left of a mutating suspend must read the PRE-mutation element ---------------
suspend fun corAAoRelay(): Int = 5
suspend fun corAAoBump(a: IntArray): Int { a[0] = 100; return corAAoRelay() }   // MUTATES a[0], then suspends
suspend fun corAAoCompute(): Int {
    val a = intArrayOf(10, 20, 30)
    return a[0] + corAAoBump(a)                // a[0] read LEFT of the suspending bump(a) -> must be 10, not 100
}

// ---- a cross-module data-class `copy` whose omitted fields are reconstructed from a SUSPENDING receiver -------
// kotlin.Triple comes from the stdlib, so kotc cannot read `copy`'s default value and reconstructs each omitted field
// as a read of the call's receiver. The receiver must be bound to ONE single-evaluation temp — and when a later
// argument suspends, that temp lives across the suspension, so the reconstructed read must also be stamped with the
// INSTANTIATED field type (an open positional type variable is unresolvable in the state machine's own frame:
// bir2cir spilled it into an SM field of that type and the first resume threw InvalidProgramException).
val corACpLog = mutableListOf<String>()
suspend fun corACpTriple(): Triple<Int, Int, Int> { corACpLog.add("T"); return Triple(1, 2, 3) }
suspend fun corACpArg(): Int { corACpLog.add("A"); return 9 }
suspend fun corACpOmitOnly(): String = corACpTriple().copy(second = 9).toString()
suspend fun corACpSuspendingArg(): String = corACpTriple().copy(second = corACpArg()).toString()

// ---- an operand whose DESUGAR carries no type stamp, spilled left of a suspension ---------------------------
// `h(x!!, susp())`: kotc lowers `x!!` to `{ var __nn = x; if (__nn != null) __nn else throw NPE }` — a valueBlock
// and a cond, neither stamped with a type, over a `local` read of the temp the block itself declares. The operand
// is impure, so it spills into an SM field ahead of the suspending argument, and a spill field must be TYPED.
// Until the shared node-local deriver read a cond through its LIVE branch (a `throw` arm says nothing about the
// value's type) and a `local` through the block's own `var`, nothing could type these and the whole function was
// REJECTED at compile time — an abort on frontend-accepted source. Four desugars reach the same stamp-less shape
// and each answers through a different slot: the reference `!!` (the block's `var`), the value-type `!!` and the
// elvis (`nullableValue.elem`), and a bare-receiver safe call (a raw `cond` whose live arm is a `nullableWrap`).
val corAUtLog = mutableListOf<String>()
suspend fun corAUtSusp(): Int { corAUtLog.add("S"); return 2 }
fun corAUtHs(a: String, b: Int): String = "$a/$b"
fun corAUtHi(a: Int, b: Int): String = "$a/$b"
fun corAUtHq(a: Int?, b: Int): String = "$a/$b"

class CorAUtBox(val name: String?) { fun size(): Int = 7 }

fun corAUtSideRef(): String? { corAUtLog.add("L"); return "v" }
fun corAUtSideVal(): Int? { corAUtLog.add("L"); return 7 }
fun corAUtSideBox(): CorAUtBox { corAUtLog.add("L"); return CorAUtBox("n") }

suspend fun corAUtRefBang(): String = corAUtHs(corAUtSideRef()!!, corAUtSusp())
suspend fun corAUtValueBang(): String = corAUtHi(corAUtSideVal()!!, corAUtSusp())
suspend fun corAUtFieldBang(): String = corAUtHs(corAUtSideBox().name!!, corAUtSusp())
suspend fun corAUtElvis(): String = corAUtHi(corAUtSideVal() ?: 0, corAUtSusp())
suspend fun corAUtSafeCall(b: CorAUtBox?): String = corAUtHq(b?.size(), corAUtSusp())

class SuspendEvaluationOrderTests {
    @TestAttribute
    fun sideEffectBeforeSuspend() {
        corAEoLog.clear()
        val v = blockOn { corAEoF() }
        assertEquals(3, v)                  // 3
        assertEquals(2, corAEoLog.size)
        assertEquals("L", corAEoLog[0])     // former golden line 1
        assertEquals("G", corAEoLog[1])     // former golden line 2
    }

    @TestAttribute
    fun rawFieldReadBeforeSuspend() {
        val b = CorAFoBox()
        assertEquals(15, blockOn { b.compute() })   // 10 + 5 = 15 (a miscompile prints 105)
        assertEquals(100, b.x)                       // bump() did run and mutate the field
    }

    @TestAttribute
    fun arrayElemReadBeforeSuspend() {
        assertEquals(15, blockOn { corAAoCompute() })   // 10 + 5 = 15 (a miscompile prints 105)
    }

    // A cross-module data-class `copy` with an omitted field, on a SUSPENDING receiver.
    @TestAttribute
    fun crossModuleCopyReceiverSuspends() {
        corACpLog.clear()
        assertEquals("(1, 9, 3)", blockOn { corACpOmitOnly() })
        assertEquals(1, corACpLog.size)                 // the suspending receiver ran ONCE (was 3)
        assertEquals("T", corACpLog[0])

        // The provided argument suspends too, so the receiver temp must survive that suspension.
        corACpLog.clear()
        assertEquals("(1, 9, 3)", blockOn { corACpSuspendingArg() })
        assertEquals(2, corACpLog.size)
        assertEquals("T", corACpLog[0])                 // receiver first
        assertEquals("A", corACpLog[1])                 // then the argument (was "T","T","A","T")
    }

    // Each of these was a COMPILE ABORT ("the operand ... carries no static type") before the desugars' own type
    // slots were read; the assertions additionally pin the left-to-right order the spill exists to preserve.
    @TestAttribute
    fun notNullAssertedReferenceBeforeSuspension() {
        corAUtLog.clear()
        assertEquals("v/2", blockOn { corAUtRefBang() })
        assertEquals(2, corAUtLog.size)
        assertEquals("L", corAUtLog[0])                 // the `!!` operand ran BEFORE the suspending argument
        assertEquals("S", corAUtLog[1])
    }

    @TestAttribute
    fun notNullAssertedValueTypeBeforeSuspension() {
        corAUtLog.clear()
        assertEquals("7/2", blockOn { corAUtValueBang() })   // `Nullable<Int>.Value`, not a boxed slot
        assertEquals(2, corAUtLog.size)
        assertEquals("L", corAUtLog[0])
    }

    @TestAttribute
    fun notNullAssertedFieldReadBeforeSuspension() {
        corAUtLog.clear()
        assertEquals("n/2", blockOn { corAUtFieldBang() })
        assertEquals(2, corAUtLog.size)
        assertEquals("L", corAUtLog[0])
    }

    @TestAttribute
    fun elvisOperandBeforeSuspension() {
        corAUtLog.clear()
        assertEquals("7/2", blockOn { corAUtElvis() })
        assertEquals(2, corAUtLog.size)
        assertEquals("L", corAUtLog[0])
    }

    @TestAttribute
    fun safeCallOperandBeforeSuspension() {
        // A bare-local receiver, so the safe call is a RAW `cond` with no `type` stamp at all.
        assertEquals("7/2", blockOn { corAUtSafeCall(CorAUtBox("q")) })
        assertEquals("null/2", blockOn { corAUtSafeCall(null) })
    }
}
