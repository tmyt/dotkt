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
// Top-level names use descriptive structured-flow, generic, array-iteration, and inline feature stems so they cannot clash with sibling
// coroutine fixtures or the stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.contracts.*
import dotkt.support.blockOn

// ---- il-coldcf: control flow across suspension (sync-completion path) ----------------------------------------
suspend fun suspendControlFlowOne(): Int = 1
suspend fun suspendControlFlowTwo(): Int = 2

suspend fun suspendControlFlowConditional(b: Boolean): Int {          // an `if` with a suspend call in each branch
    val x = if (b) suspendControlFlowOne() else suspendControlFlowTwo()
    return x + 10
}
suspend fun suspendControlFlowWhileLoop(n: Int): Int {              // a `while` summing N suspend-call results
    var acc = 0
    var i = 0
    while (i < n) { acc = acc + suspendControlFlowOne(); i = i + 1 }
    return acc
}
suspend fun suspendControlFlowWhenExpression(n: Int): Int {              // a `when` with a suspension in a branch
    val x = when (n) {
        0 -> suspendControlFlowOne()
        1 -> suspendControlFlowTwo()
        else -> 99
    }
    return x
}
suspend fun suspendControlFlowForLoop(xs: List<Int>): Int {       // a `for (e in xs)` with a suspend call in the body
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowOne() }
    return acc
}
suspend fun suspendControlFlowTryCatch(fail: Boolean): Int {      // a suspension in the try BODY; catch catches a post-resume throw
    try {
        val x = suspendControlFlowOne()
        if (fail) throw IllegalStateException("after resume")
        return x + 100
    } catch (e: Exception) {
        return -1
    }
}
suspend fun Int.suspendControlFlowPlusOne(): Int = this + 1  // a suspend extension fun (receiver -> `__self`)

// ---- il-coldgen: the generic SM spike ------------------------------------------------------------------------
suspend fun <T> suspendControlFlowGenericIdentity(x: T): T = x                // no suspension -> a generic direct cold entry
suspend fun <T> suspendControlFlowGenericPassthrough(x: T): T {             // generic + a suspend call (await temp typed T)
    val y = suspendControlFlowGenericIdentity(x)
    return y
}

// ---- il-coforarray: suspend control flow over a `for (e in ARRAY)` loop ---------------------------------------
suspend fun suspendControlFlowArrayIterationOne(): Int = 1
suspend fun suspendControlFlowArrayIterationOverArray(xs: Array<Int>): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowArrayIterationOne() }
    return acc
}
suspend fun suspendControlFlowArrayIterationOverVararg(vararg xs: Int): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowArrayIterationOne() }
    return acc
}
suspend fun suspendControlFlowArrayIterationOverIntArray(xs: IntArray): Int {
    var acc = 0
    for (e in xs) { acc = acc + e + suspendControlFlowArrayIterationOne() }
    return acc
}

// ---- il-coinline: suspend inline + crossinline over the unintercepted intrinsic (#22) ------------------------
@OptIn(ExperimentalContracts::class)
suspend inline fun <T> suspendControlFlowInlineSuspend(crossinline block: (Continuation<T>) -> Unit): T {
    contract { callsInPlace(block, InvocationKind.EXACTLY_ONCE) }
    return suspendCoroutineUninterceptedOrReturn { uCont ->
        block(uCont)
        COROUTINE_SUSPENDED
    }
}
suspend fun suspendControlFlowInlineCaller(): Int = suspendControlFlowInlineSuspend { cont -> cont.resume(5) }
suspend fun suspendControlFlowInlineOther(): Int = suspendControlFlowInlineSuspend { cont -> cont.resume(37) }

class SuspendControlFlowTests {
    @TestAttribute
    fun controlFlowAcrossSuspension() {
        assertEquals(11, blockOn { suspendControlFlowConditional(true) })            // 11
        assertEquals(12, blockOn { suspendControlFlowConditional(false) })           // 12
        assertEquals(3, blockOn { suspendControlFlowWhileLoop(3) })                // 3
        assertEquals(1, blockOn { suspendControlFlowWhenExpression(0) })                // 1
        assertEquals(2, blockOn { suspendControlFlowWhenExpression(1) })                // 2
        assertEquals(99, blockOn { suspendControlFlowWhenExpression(5) })               // 99
        assertEquals(32, blockOn { suspendControlFlowForLoop(listOf(10, 20)) })  // 32
        assertEquals(101, blockOn { suspendControlFlowTryCatch(false) })         // 101
        assertEquals(-1, blockOn { suspendControlFlowTryCatch(true) })           // -1
        assertEquals(42, blockOn { 41.suspendControlFlowPlusOne() })        // 42
    }

    @TestAttribute
    fun genericStateMachine() {
        assertEquals(7, blockOn { suspendControlFlowGenericIdentity(7) })           // value T = Int
        assertEquals("yo", blockOn { suspendControlFlowGenericIdentity("yo") })     // reference T = String
        assertEquals(8, blockOn { suspendControlFlowGenericPassthrough(8) })      // value T through a suspension
        assertEquals("hi", blockOn { suspendControlFlowGenericPassthrough("hi") })// reference T through a suspension
    }

    @TestAttribute
    fun forOverArrayWithSuspend() {
        assertEquals(63, blockOn { suspendControlFlowArrayIterationOverArray(arrayOf(10, 20, 30)) })   // (10+1)+(20+1)+(30+1)=63
        assertEquals(63, blockOn { suspendControlFlowArrayIterationOverVararg(10, 20, 30) })           // 63
        assertEquals(9, blockOn { suspendControlFlowArrayIterationOverIntArray(intArrayOf(1, 2, 3)) }) // (1+1)+(2+1)+(3+1)=9
    }

    @TestAttribute
    fun crossinlineOverUninterceptedIntrinsic() {
        assertEquals(42, blockOn { suspendControlFlowInlineCaller() + suspendControlFlowInlineOther() })   // 5 + 37 = 42
    }
}
