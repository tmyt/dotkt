// Suspend functional-VALUE + this-capture battery — migrates the `suspend (...) -> T` value-invoke and
// enclosing-instance-capture coroutine cases onto the in-process NUnit suite. Driven by the shared
// `dotkt.support.blockOn` harness. Each old case's `main` + stdout-golden becomes one @TestAttribute method
// preserving every asserted value 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-suspendvalue   -> suspendvalue_paramValueAndHigherOrder
//                        invoking a suspend functional VALUE `b()` (a SuspendFunctionN, no named cold entry) —
//                        driven through startSuspendUninterceptedOrReturn: a suspend PARAM value, the
//                        higher-order times/repeat idiom, a suspend value in a LOCAL, and a this-capturing
//                        member-built suspend lambda (GAP 2).
//   il-suspendcapture -> suspendcapture_enclosingInstanceCapture
//                        (#34a) a suspend LAMBDA closing over its enclosing instance's members — the SuspendLambda
//                        SM captures the instance as `__outer`; `this.member` reads must redirect to it. Covered
//                        in every construction position (value / call-arg / via member method / object receiver /
//                        nested lambda) + a local-capture control.
//
// Top-level names are family-prefixed (`sv`/`scap`) so they can't clash with sibling coroutine fixtures or the
// stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- il-suspendvalue -----------------------------------------------------------------------------------------
suspend fun svAddA(a: Int, b: Int): Int = a + b

suspend fun svRun1(b: suspend () -> Int): Int = b()                     // invoke a suspend PARAM value

suspend fun svTimes(n: Int, block: suspend () -> Unit) {               // the higher-order repeat idiom
    var i = 0
    while (i < n) { block(); i++ }
}

suspend fun svLocal1(): Int {                                          // a suspend value in a LOCAL, then invoked
    val f: suspend () -> Int = { svAddA(37, 5) }
    return f()
}

class SvBox(val n: Int) {
    suspend fun go(): Int = svRun1 { svAddA(n, 5) }                    // GAP 2: member builds a this-capturing lambda
}

// ---- il-suspendcapture ---------------------------------------------------------------------------------------
suspend fun scapAddA(a: Int, b: Int): Int = a + b

class ScapBox(val n: Int) {
    fun makeVal(): suspend () -> Int = { scapAddA(n, 5) }                       // VALUE position: captures this.n
    fun runArg(): Int = blockOn { scapAddA(n, 5) }                             // CALL-ARG position: captures this.n
    fun bump(): Int = n * 2
    fun runMethod(): Int = blockOn { scapAddA(bump(), 1) }                     // this-capture via a member method
    fun outer(): suspend () -> Int = { scapAddA(n, blockOn { scapAddA(n, 0) }) } // NESTED capturing suspend lambda
}

object ScapHolder {
    val base: Int = 100
    fun runObj(): Int = blockOn { scapAddA(base, 5) }                          // object-receiver capture
}

fun scapMk(k: Int): suspend () -> Int = { scapAddA(k, 5) }                     // LOCAL capture (non-regression control)

// BIR local slots are declaration identities, not Kotlin source spellings. This deliberately keeps two `value`
// declarations of different types alive around a genuinely asynchronous resume; bir2cir must spill the slots already
// named by kotc, without reconstructing lexical shadowing from nested JSON.
suspend fun scapShadowedFrameSlots(): String {
    val value = 1
    val resumed = suspendContextAsyncResume()
    val rendered = run {
        val value = "inner"
        value
    }
    return value.toString() + ":" + resumed.toString() + ":" + rendered
}

class SuspendCaptureTests {
    @TestAttribute
    fun paramValueAndHigherOrder() {
        assertEquals(42, blockOn { svRun1 { svAddA(37, 5) } })   // 42
        var acc = 0
        blockOn { svTimes(3) { acc += 14 } }
        assertEquals(42, acc)                                    // 42
        assertEquals(42, blockOn { svLocal1() })                 // 42
        assertEquals(42, blockOn { SvBox(37).go() })             // 42
    }

    @TestAttribute
    fun enclosingInstanceCapture() {
        assertEquals(42, blockOn(ScapBox(37).makeVal()))   // 42
        assertEquals(42, ScapBox(37).runArg())             // 42
        assertEquals(41, ScapBox(20).runMethod())          // 41
        assertEquals(40, blockOn(ScapBox(20).outer()))     // 40
        assertEquals(105, ScapHolder.runObj())             // 105
        assertEquals(42, blockOn(scapMk(37)))              // 42
        assertEquals("1:42:inner", blockOn { scapShadowedFrameSlots() })
    }
}
