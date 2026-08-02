// Cold-core suspend control-flow / generic-SM / for-over-array / inline-suspend battery (feature fixture). Migrates the
// `suspend fun main`-driven cold state-machine cases onto the in-process NUnit suite: each old `main` + stdout golden
// becomes one @TestAttribute method driven by the shared `dotkt.support.blockOn` cold-core harness, asserting every
// value 1:1 (`// <expected>`). No genuine .NET async here — every suspension completes synchronously on the cold path.
//
// Coverage preserved (old case -> method):
//   il-coldcf     -> coldCf_controlFlowAcrossSuspension   (if/when/while/for + try/catch + suspend extension fun)
//   il-coldgen    -> coldGen_genericStateMachine          (generic `suspend fun <T>` cold SM; value + reference T)
//   il-coforarray -> coForArray_forOverArrayWithSuspend   (forArray node: Array<Int> / vararg / IntArray)
//   il-coinline   -> coInline_crossinlineOverUninterceptedIntrinsic  (#22 InlineSplice: suspend inline + crossinline
//                    invoked inside suspendCoroutineUninterceptedOrReturn, synchronous cont.resume re-entry)
//
// Top-level names are family-prefixed (`suspendControlFlowCf`/`suspendControlFlowGen`/`suspendControlFlowFa`/`suspendControlFlowInl`) so they can't clash with sibling
// coroutine fixtures or the stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.contracts.*
import dotkt.support.blockOn

// ---- il-coldcf: control flow across suspension (sync-completion path) ----------------------------------------
suspend fun suspendControlFlowCfOne(): Int = 1
suspend fun suspendControlFlowCfTwo(): Int = 2

suspend fun suspendControlFlowCfCf1(b: Boolean): Int {          // an `if` with a suspend call in each branch
    val x = if (b) suspendControlFlowCfOne() else suspendControlFlowCfTwo()
    return x + 10
}
suspend fun suspendControlFlowCfCf2(n: Int): Int {              // a `while` summing N suspend-call results
    var acc = 0
    var i = 0
    while (i < n) { acc = acc + suspendControlFlowCfOne(); i = i + 1 }
    return acc
}
suspend fun suspendControlFlowCfCf3(n: Int): Int {              // a `when` with a suspension in a branch
    val x = when (n) {
        0 -> suspendControlFlowCfOne()
        1 -> suspendControlFlowCfTwo()
        else -> 99
    }
    return x
}
suspend fun suspendControlFlowCfCf4(xs: List<Int>): Int {       // a `for (e in xs)` with a suspend call in the body
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowCfOne() }
    return acc
}
suspend fun suspendControlFlowCfExc1(fail: Boolean): Int {      // a suspension in the try BODY; catch catches a post-resume throw
    try {
        val x = suspendControlFlowCfOne()
        if (fail) throw IllegalStateException("after resume")
        return x + 100
    } catch (e: Exception) {
        return -1
    }
}
suspend fun Int.suspendControlFlowCfPlusOneS(): Int = this + 1  // a suspend extension fun (receiver -> `__self`)

// ---- il-coldgen: the generic SM spike ------------------------------------------------------------------------
suspend fun <T> suspendControlFlowGenIdw(x: T): T = x                // no suspension -> a generic direct cold entry
suspend fun <T> suspendControlFlowGenPassthru(x: T): T {             // generic + a suspend call (await temp typed T)
    val y = suspendControlFlowGenIdw(x)
    return y
}

// ---- il-coforarray: suspend control flow over a `for (e in ARRAY)` loop ---------------------------------------
suspend fun suspendControlFlowFaOne(): Int = 1
suspend fun suspendControlFlowFaOverArray(xs: Array<Int>): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowFaOne() }
    return acc
}
suspend fun suspendControlFlowFaOverVararg(vararg xs: Int): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowFaOne() }
    return acc
}
suspend fun suspendControlFlowFaOverIntArray(xs: IntArray): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowFaOne() }
    return acc
}

// ---- il-coinline: suspend inline + crossinline over the unintercepted intrinsic (#22) ------------------------
@OptIn(ExperimentalContracts::class)
suspend inline fun <T> suspendControlFlowInlMySuspend(crossinline block: (Continuation<T>) -> Unit): T {
    contract { callsInPlace(block, InvocationKind.EXACTLY_ONCE) }
    return suspendCoroutineUninterceptedOrReturn { uCont ->
        block(uCont)
        COROUTINE_SUSPENDED
    }
}
suspend fun suspendControlFlowInlCaller(): Int = suspendControlFlowInlMySuspend { cont -> cont.resume(5) }
suspend fun suspendControlFlowInlOther(): Int = suspendControlFlowInlMySuspend { cont -> cont.resume(37) }

class SuspendControlFlowTests {
    @TestAttribute
    fun controlFlowAcrossSuspension() {
        assertEquals(11, blockOn { suspendControlFlowCfCf1(true) })            // 11
        assertEquals(12, blockOn { suspendControlFlowCfCf1(false) })           // 12
        assertEquals(3, blockOn { suspendControlFlowCfCf2(3) })                // 3
        assertEquals(1, blockOn { suspendControlFlowCfCf3(0) })                // 1
        assertEquals(2, blockOn { suspendControlFlowCfCf3(1) })                // 2
        assertEquals(99, blockOn { suspendControlFlowCfCf3(5) })               // 99
        assertEquals(32, blockOn { suspendControlFlowCfCf4(listOf(10, 20)) })  // 32
        assertEquals(101, blockOn { suspendControlFlowCfExc1(false) })         // 101
        assertEquals(-1, blockOn { suspendControlFlowCfExc1(true) })           // -1
        assertEquals(42, blockOn { 41.suspendControlFlowCfPlusOneS() })        // 42
    }

    @TestAttribute
    fun genericStateMachine() {
        assertEquals(7, blockOn { suspendControlFlowGenIdw(7) })           // value T = Int
        assertEquals("yo", blockOn { suspendControlFlowGenIdw("yo") })     // reference T = String
        assertEquals(8, blockOn { suspendControlFlowGenPassthru(8) })      // value T through a suspension
        assertEquals("hi", blockOn { suspendControlFlowGenPassthru("hi") })// reference T through a suspension
    }

    @TestAttribute
    fun forOverArrayWithSuspend() {
        assertEquals(63, blockOn { suspendControlFlowFaOverArray(arrayOf(10, 20, 30)) })   // (10+1)+(20+1)+(30+1)=63
        assertEquals(63, blockOn { suspendControlFlowFaOverVararg(10, 20, 30) })           // 63
        assertEquals(9, blockOn { suspendControlFlowFaOverIntArray(intArrayOf(1, 2, 3)) }) // (1+1)+(2+1)+(3+1)=9
    }

    @TestAttribute
    fun crossinlineOverUninterceptedIntrinsic() {
        assertEquals(42, blockOn { suspendControlFlowInlCaller() + suspendControlFlowInlOther() })   // 5 + 37 = 42
    }
}
