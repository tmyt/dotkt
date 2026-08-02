// feature fixture — il-suspendnestedcapture: a `suspend inline fun` with a `crossinline` block whose body NESTS a lambda
// capturing an enclosing binding (the `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }` shape).
// All top-level declarations use the descriptive `nestedCapture`/`NestedCapture` stem so their simple names are
// UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name; note
// this case's `nestedCaptureSuspend` must not clash with il-inlmatsetcap's `materializedCaptureSuspend`). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

suspend inline fun <T> nestedCaptureSuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

fun nestedCaptureRegister(action: () -> Unit) { action() }

suspend fun nestedCaptureConstant(): Int = nestedCaptureSuspend { cont -> nestedCaptureRegister { cont.resume(5) } }
suspend fun nestedCaptureParameter(h: Int): Int = nestedCaptureSuspend { cont -> nestedCaptureRegister { cont.resume(h + 1) } }
suspend fun <T> nestedCaptureGeneric(v: T): T = nestedCaptureSuspend { cont ->
    val local = v
    nestedCaptureRegister { cont.resume(local) }
}
suspend fun nestedCaptureArrayForEach(): Int = nestedCaptureSuspend { cont ->
    arrayOf(7).forEach { nestedCaptureRegister { cont.resume(it) } }
}
suspend fun nestedCaptureMap(): Int = nestedCaptureSuspend { cont ->
    arrayOf(50).map { nestedCaptureRegister { cont.resume(it) }; it }
}
suspend fun nestedCaptureForEachIndexed(): Int = nestedCaptureSuspend { cont ->
    arrayOf(100).forEachIndexed { idx, v -> nestedCaptureRegister { cont.resume(idx + v) } }
}
suspend fun nestedCaptureListForEach(): Int = nestedCaptureSuspend { cont ->
    listOf(70).forEach { nestedCaptureRegister { cont.resume(it) } }
}
suspend fun nestedCaptureCatch(): Int = nestedCaptureSuspend { cont ->
    try { throw RuntimeException("80") } catch (e: RuntimeException) { nestedCaptureRegister { cont.resume(e.message!!.toInt()) } }
}

class NestedSuspendCaptureTests {
    @TestAttribute
    fun nestedClosureInCarrier() {
        assertEquals(5, blockOn { nestedCaptureConstant() })          // 5
        assertEquals(42, blockOn { nestedCaptureParameter(41) })       // 42
        assertEquals("hi", blockOn { nestedCaptureGeneric("hi") })   // hi
        assertEquals(7, blockOn { nestedCaptureGeneric(7) })         // 7
        assertEquals(7, blockOn { nestedCaptureArrayForEach() })         // 7
        assertEquals(50, blockOn { nestedCaptureMap() })       // 50
        assertEquals(100, blockOn { nestedCaptureForEachIndexed() })      // 100
        assertEquals(70, blockOn { nestedCaptureListForEach() })      // 70
        assertEquals(80, blockOn { nestedCaptureCatch() })       // 80
    }
}
