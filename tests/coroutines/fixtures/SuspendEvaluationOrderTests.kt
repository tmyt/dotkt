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
}
