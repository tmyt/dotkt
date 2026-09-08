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
// Top-level names use the descriptive `suspendValue` and `suspendCapture` stems so they remain readable and cannot
// clash with sibling coroutine fixtures or the stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Type
import System.Threading.Tasks.Task1
import dotkt.support.blockOn

// ---- il-suspendvalue -----------------------------------------------------------------------------------------
suspend fun suspendValueAdd(a: Int, b: Int): Int = a + b

suspend fun suspendValueInvoke(b: suspend () -> Int): Int = b()                     // invoke a suspend PARAM value

suspend fun suspendValueTimes(n: Int, block: suspend () -> Unit) {               // the higher-order repeat idiom
    var i = 0
    while (i < n) { block(); i++ }
}

suspend fun suspendValueLocal(): Int {                                          // a suspend value in a LOCAL, then invoked
    val f: suspend () -> Int = { suspendValueAdd(37, 5) }
    return f()
}

class SuspendValueBox(val n: Int) {
    suspend fun go(): Int = suspendValueInvoke { suspendValueAdd(n, 5) }                    // GAP 2: member builds a this-capturing lambda
}

// ---- il-suspendcapture ---------------------------------------------------------------------------------------
suspend fun suspendCaptureAdd(a: Int, b: Int): Int = a + b

class SuspendCaptureBox(val n: Int) {
    fun makeVal(): suspend () -> Int = { suspendCaptureAdd(n, 5) }                       // VALUE position: captures this.n
    fun runArg(): Int = blockOn { suspendCaptureAdd(n, 5) }                             // CALL-ARG position: captures this.n
    fun bump(): Int = n * 2
    fun runMethod(): Int = blockOn { suspendCaptureAdd(bump(), 1) }                     // this-capture via a member method
    fun outer(): suspend () -> Int = { suspendCaptureAdd(n, blockOn { suspendCaptureAdd(n, 0) }) } // NESTED capturing suspend lambda
}

class SuspendCaptureOuterParameterCollision(val base: Int) {
    // `__outer` is an ordinary Kotlin parameter name. Its String-typed create() slot must remain distinct from the
    // enclosing-instance capture field even though the compiler historically used that spelling for the latter.
    fun make(): suspend (String) -> Int = { __outer -> suspendCaptureAdd(base, __outer.length) }
}

object SuspendCaptureHolder {
    val base: Int = 100
    fun runObj(): Int = blockOn { suspendCaptureAdd(base, 5) }                          // object-receiver capture
}

fun suspendCaptureMake(k: Int): suspend () -> Int = { suspendCaptureAdd(k, 5) }                     // LOCAL capture (non-regression control)

suspend fun suspendCaptureStringWork(): String = suspendContextAsyncResume().toString()

suspend fun suspendCaptureAssignFromSuspendCaller(): String {
    suspendContextAsyncResume()
    var result = ""
    blockOn { result = suspendCaptureStringWork() }
    return result
}

suspend fun suspendStorageMachineryParameterNames(label: String, completion: String, result: String): String {
    suspendContextAsyncResume()
    return label + completion + result
}

suspend fun suspendStorageDirectCompletionName(completion: String): String = completion

class SuspendStorageMember {
    suspend fun combine(label: String, completion: String, result: String): String {
        suspendContextAsyncResume()
        return label + completion + result
    }
}

suspend fun suspendStorageGeneratedLocalNames(
    __sm: String,
    __tcs: String,
    __root: String,
    __r: String,
    __e: String,
): String {
    suspendContextAsyncResume()
    return __sm + __tcs + __root + __r + __e
}

private fun invokeSuspendStorageGeneratedLocalNamesTask(): Task1<String> =
    Type.GetType("SuspendCaptureTestsKt")!!
        .GetMethod("suspendStorageGeneratedLocalNames")!!
        .Invoke(null, arrayOf<Any?>("SM", "TCS", "ROOT", "RESULT", "EX")) as Task1<String>

// BIR local slots are declaration identities, not Kotlin source spellings. This deliberately keeps two `value`
// declarations of different types alive around a genuinely asynchronous resume; bir2cir must spill the slots already
// named by kotc, without reconstructing lexical shadowing from nested JSON.
suspend fun suspendCaptureShadowedFrameSlots(): String {
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
        assertEquals(42, blockOn { suspendValueInvoke { suspendValueAdd(37, 5) } })   // 42
        var acc = 0
        blockOn { suspendValueTimes(3) { acc += 14 } }
        assertEquals(42, acc)                                    // 42
        assertEquals(42, blockOn { suspendValueLocal() })                 // 42
        assertEquals(42, blockOn { SuspendValueBox(37).go() })             // 42
    }

    @TestAttribute
    fun enclosingInstanceCapture() {
        assertEquals(42, blockOn(SuspendCaptureBox(37).makeVal()))   // 42
        assertEquals(42, SuspendCaptureBox(37).runArg())             // 42
        assertEquals(41, SuspendCaptureBox(20).runMethod())          // 41
        assertEquals(40, blockOn(SuspendCaptureBox(20).outer()))     // 40
        assertEquals(105, SuspendCaptureHolder.runObj())             // 105
        assertEquals(42, blockOn(suspendCaptureMake(37)))              // 42
        val colliding = SuspendCaptureOuterParameterCollision(37).make()
        assertEquals(42, blockOn { colliding("12345") })
        assertEquals("1:42:inner", blockOn { suspendCaptureShadowedFrameSlots() })
    }

    @TestAttribute
    fun suspendingResultAssignsThroughCapturedVar() {
        var result = ""
        var count = 0
        blockOn { result = suspendCaptureStringWork() }
        blockOn { count = suspendContextAsyncResume() }
        assertEquals("42", result)
        assertEquals(42, count)
        assertEquals("42", blockOn { suspendCaptureAssignFromSuspendCaller() })
    }

    @TestAttribute
    fun suspendStorageNamesRemainDisjoint() {
        assertEquals("LCR", blockOn { suspendStorageMachineryParameterNames("L", "C", "R") })
        assertEquals("direct", blockOn { suspendStorageDirectCompletionName("direct") })
        assertEquals("LCR", blockOn { SuspendStorageMember().combine("L", "C", "R") })
        assertEquals("SMTCSROOTRESULTEX", invokeSuspendStorageGeneratedLocalNamesTask().Result)

        val lambda: suspend (String, String, String) -> String = { label, completion, result ->
            suspendContextAsyncResume()
            label + completion + result
        }
        assertEquals("LCR", blockOn { lambda("L", "C", "R") })
    }
}
