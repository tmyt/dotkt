// CorB batch — il-suspendnestedcapture: a `suspend inline fun` with a `crossinline` block whose body NESTS a lambda
// capturing an enclosing binding (the `suspendCancellableCoroutine { cont -> cont.invokeOnCancellation { … } }` shape).
// All top-level decls carry the `snc` case token under the shared `corB`/`CorB` prefix so their simple names are
// UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name; note
// this case's `corBSncMySuspend` must not clash with il-inlmatsetcap's `corBImscMySuspend`). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

suspend inline fun <T> corBSncMySuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

fun corBSncRegister(action: () -> Unit) { action() }

suspend fun corBSncCap0(): Int = corBSncMySuspend { cont -> corBSncRegister { cont.resume(5) } }
suspend fun corBSncCap1(h: Int): Int = corBSncMySuspend { cont -> corBSncRegister { cont.resume(h + 1) } }
suspend fun <T> corBSncCapG(v: T): T = corBSncMySuspend { cont ->
    val local = v
    corBSncRegister { cont.resume(local) }
}
suspend fun corBSncCapFE(): Int = corBSncMySuspend { cont ->
    arrayOf(7).forEach { corBSncRegister { cont.resume(it) } }
}
suspend fun corBSncCapMap(): Int = corBSncMySuspend { cont ->
    arrayOf(50).map { corBSncRegister { cont.resume(it) }; it }
}
suspend fun corBSncCapFEI(): Int = corBSncMySuspend { cont ->
    arrayOf(100).forEachIndexed { idx, v -> corBSncRegister { cont.resume(idx + v) } }
}
suspend fun corBSncCapList(): Int = corBSncMySuspend { cont ->
    listOf(70).forEach { corBSncRegister { cont.resume(it) } }
}
suspend fun corBSncCapTry(): Int = corBSncMySuspend { cont ->
    try { throw RuntimeException("80") } catch (e: RuntimeException) { corBSncRegister { cont.resume(e.message!!.toInt()) } }
}

class CorBSuspendNestedCaptureTests {
    @TestAttribute
    fun suspendnestedcapture_nestedClosureInCarrier() {
        assertEquals(5, blockOn { corBSncCap0() })          // 5
        assertEquals(42, blockOn { corBSncCap1(41) })       // 42
        assertEquals("hi", blockOn { corBSncCapG("hi") })   // hi
        assertEquals(7, blockOn { corBSncCapG(7) })         // 7
        assertEquals(7, blockOn { corBSncCapFE() })         // 7
        assertEquals(50, blockOn { corBSncCapMap() })       // 50
        assertEquals(100, blockOn { corBSncCapFEI() })      // 100
        assertEquals(70, blockOn { corBSncCapList() })      // 70
        assertEquals(80, blockOn { corBSncCapTry() })       // 80
    }
}
