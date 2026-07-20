// Cold-core suspend control-flow / generic-SM / for-over-array / inline-suspend battery (CorA batch). Migrates the
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
// Top-level names are family-prefixed (`corACf`/`corAGen`/`corAFa`/`corAInl`) so they can't clash with sibling
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
suspend fun corACfOne(): Int = 1
suspend fun corACfTwo(): Int = 2

suspend fun corACfCf1(b: Boolean): Int {          // an `if` with a suspend call in each branch
    val x = if (b) corACfOne() else corACfTwo()
    return x + 10
}
suspend fun corACfCf2(n: Int): Int {              // a `while` summing N suspend-call results
    var acc = 0
    var i = 0
    while (i < n) { acc = acc + corACfOne(); i = i + 1 }
    return acc
}
suspend fun corACfCf3(n: Int): Int {              // a `when` with a suspension in a branch
    val x = when (n) {
        0 -> corACfOne()
        1 -> corACfTwo()
        else -> 99
    }
    return x
}
suspend fun corACfCf4(xs: List<Int>): Int {       // a `for (e in xs)` with a suspend call in the body
    var acc = 0
    for (e in xs) { acc = acc + e + corACfOne() }
    return acc
}
suspend fun corACfExc1(fail: Boolean): Int {      // a suspension in the try BODY; catch catches a post-resume throw
    try {
        val x = corACfOne()
        if (fail) throw IllegalStateException("after resume")
        return x + 100
    } catch (e: Exception) {
        return -1
    }
}
suspend fun Int.corACfPlusOneS(): Int = this + 1  // a suspend extension fun (receiver -> `__self`)

// ---- il-coldgen: the generic SM spike ------------------------------------------------------------------------
suspend fun <T> corAGenIdw(x: T): T = x                // no suspension -> a generic direct cold entry
suspend fun <T> corAGenPassthru(x: T): T {             // generic + a suspend call (await temp typed T)
    val y = corAGenIdw(x)
    return y
}

// ---- il-coforarray: suspend control flow over a `for (e in ARRAY)` loop ---------------------------------------
suspend fun corAFaOne(): Int = 1
suspend fun corAFaOverArray(xs: Array<Int>): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + corAFaOne() }
    return acc
}
suspend fun corAFaOverVararg(vararg xs: Int): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + corAFaOne() }
    return acc
}
suspend fun corAFaOverIntArray(xs: IntArray): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + corAFaOne() }
    return acc
}

// ---- il-coinline: suspend inline + crossinline over the unintercepted intrinsic (#22) ------------------------
@OptIn(ExperimentalContracts::class)
suspend inline fun <T> corAInlMySuspend(crossinline block: (Continuation<T>) -> Unit): T {
    contract { callsInPlace(block, InvocationKind.EXACTLY_ONCE) }
    return suspendCoroutineUninterceptedOrReturn { uCont ->
        block(uCont)
        COROUTINE_SUSPENDED
    }
}
suspend fun corAInlCaller(): Int = corAInlMySuspend { cont -> cont.resume(5) }
suspend fun corAInlOther(): Int = corAInlMySuspend { cont -> cont.resume(37) }

class CorAColdFlowTests {
    @TestAttribute
    fun coldCf_controlFlowAcrossSuspension() {
        assertEquals(11, blockOn { corACfCf1(true) })            // 11
        assertEquals(12, blockOn { corACfCf1(false) })           // 12
        assertEquals(3, blockOn { corACfCf2(3) })                // 3
        assertEquals(1, blockOn { corACfCf3(0) })                // 1
        assertEquals(2, blockOn { corACfCf3(1) })                // 2
        assertEquals(99, blockOn { corACfCf3(5) })               // 99
        assertEquals(32, blockOn { corACfCf4(listOf(10, 20)) })  // 32
        assertEquals(101, blockOn { corACfExc1(false) })         // 101
        assertEquals(-1, blockOn { corACfExc1(true) })           // -1
        assertEquals(42, blockOn { 41.corACfPlusOneS() })        // 42
    }

    @TestAttribute
    fun coldGen_genericStateMachine() {
        assertEquals(7, blockOn { corAGenIdw(7) })           // value T = Int
        assertEquals("yo", blockOn { corAGenIdw("yo") })     // reference T = String
        assertEquals(8, blockOn { corAGenPassthru(8) })      // value T through a suspension
        assertEquals("hi", blockOn { corAGenPassthru("hi") })// reference T through a suspension
    }

    @TestAttribute
    fun coForArray_forOverArrayWithSuspend() {
        assertEquals(63, blockOn { corAFaOverArray(arrayOf(10, 20, 30)) })   // (10+1)+(20+1)+(30+1)=63
        assertEquals(63, blockOn { corAFaOverVararg(10, 20, 30) })           // 63
        assertEquals(9, blockOn { corAFaOverIntArray(intArrayOf(1, 2, 3)) }) // (1+1)+(2+1)+(3+1)=9
    }

    @TestAttribute
    fun coInline_crossinlineOverUninterceptedIntrinsic() {
        assertEquals(42, blockOn { corAInlCaller() + corAInlOther() })   // 5 + 37 = 42
    }
}
