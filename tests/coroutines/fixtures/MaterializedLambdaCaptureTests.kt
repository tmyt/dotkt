// CorB batch — il-inlmatsetcap: §4.4ii ref-cell write-through — a MATERIALIZED (non-suspend newClosure) carrier that
// MUTATES a captured enclosing var must write through so the mutation is visible after the inline call. All top-level
// decls carry the `imsc` case token under the shared `corB`/`CorB` prefix so their simple names are UNIQUE across this
// assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). Driven by the shared
// `dotkt.support.blockOn` harness; the former `main` + golden -> one @TestAttribute method (value 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import dotkt.support.blockOn

suspend inline fun <T> corBImscMySuspend(crossinline block: (Continuation<T>) -> Unit): T =
    suspendCoroutineUninterceptedOrReturn { uCont -> block(uCont); COROUTINE_SUSPENDED }

suspend fun corBImscCapWrite(): Int {
    var acc = 0
    return corBImscMySuspend { cont -> acc += 10; cont.resume(acc) }
}

class MaterializedLambdaCaptureTests {
    @TestAttribute
    fun refCellWriteThroughMaterializedCarrier() {
        assertEquals(10, blockOn { corBImscCapWrite() })   // 10 — ref-cell write-through through the materialized carrier
    }
}
