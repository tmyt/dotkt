// feature fixture — il-suspendnestedcapture: a `suspend inline fun` with a `crossinline` block whose body NESTS a lambda
// capturing an enclosing binding (the `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }` shape).
// All top-level decls carry the `snc` case token under the shared `nestedCapture`/`NestedCapture` prefix so their simple names are
// UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name; note
// this case's `nestedCaptureSncMySuspend` must not clash with il-inlmatsetcap's `materializedCaptureSuspend`). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

suspend inline fun <T> nestedCaptureSncMySuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

fun nestedCaptureSncRegister(action: () -> Unit) { action() }

suspend fun nestedCaptureSncCap0(): Int = nestedCaptureSncMySuspend { cont -> nestedCaptureSncRegister { cont.resume(5) } }
suspend fun nestedCaptureSncCap1(h: Int): Int = nestedCaptureSncMySuspend { cont -> nestedCaptureSncRegister { cont.resume(h + 1) } }
suspend fun <T> nestedCaptureSncCapG(v: T): T = nestedCaptureSncMySuspend { cont ->
    val local = v
    nestedCaptureSncRegister { cont.resume(local) }
}
suspend fun nestedCaptureSncCapFE(): Int = nestedCaptureSncMySuspend { cont ->
    arrayOf(7).forEach { nestedCaptureSncRegister { cont.resume(it) } }
}
suspend fun nestedCaptureSncCapMap(): Int = nestedCaptureSncMySuspend { cont ->
    arrayOf(50).map { nestedCaptureSncRegister { cont.resume(it) }; it }
}
suspend fun nestedCaptureSncCapFEI(): Int = nestedCaptureSncMySuspend { cont ->
    arrayOf(100).forEachIndexed { idx, v -> nestedCaptureSncRegister { cont.resume(idx + v) } }
}
suspend fun nestedCaptureSncCapList(): Int = nestedCaptureSncMySuspend { cont ->
    listOf(70).forEach { nestedCaptureSncRegister { cont.resume(it) } }
}
suspend fun nestedCaptureSncCapTry(): Int = nestedCaptureSncMySuspend { cont ->
    try { throw RuntimeException("80") } catch (e: RuntimeException) { nestedCaptureSncRegister { cont.resume(e.message!!.toInt()) } }
}

class NestedSuspendCaptureTests {
    @TestAttribute
    fun nestedClosureInCarrier() {
        assertEquals(5, blockOn { nestedCaptureSncCap0() })          // 5
        assertEquals(42, blockOn { nestedCaptureSncCap1(41) })       // 42
        assertEquals("hi", blockOn { nestedCaptureSncCapG("hi") })   // hi
        assertEquals(7, blockOn { nestedCaptureSncCapG(7) })         // 7
        assertEquals(7, blockOn { nestedCaptureSncCapFE() })         // 7
        assertEquals(50, blockOn { nestedCaptureSncCapMap() })       // 50
        assertEquals(100, blockOn { nestedCaptureSncCapFEI() })      // 100
        assertEquals(70, blockOn { nestedCaptureSncCapList() })      // 70
        assertEquals(80, blockOn { nestedCaptureSncCapTry() })       // 80
    }
}
