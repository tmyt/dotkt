// feature fixture — il-inlsuspendouter: the `__outer` receiver rebind (#75 BATCH B, 2B, the 52nd site). An EXTENSION
// `inline fun T.op` whose body builds a SOURCE `newSuspendLambda` (the `blockOn { … }` arg) capturing `this@op` (the
// enclosing extension receiver, kotc's `__outer`), rebound to the splice's `__self` temp by 2B. All top-level decls
// use the `suspendExtensionReceiverOuterCapture`/`SuspendExtensionReceiverOuterCapture` stem so their simple names are UNIQUE across this
// assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). The former `main` +
// golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun suspendExtensionReceiverOuterCaptureAdd(a: Int, b: Int): Int = a + b

// extension inline fun; the payload's `blockOn { … }` lambda (a SOURCE newSuspendLambda) captures `this@suspendExtensionReceiverOuterCaptureOperation`
// (= __outer, the extension receiver) alongside the crossinline suspend carrier `f`.
inline fun <T> T.suspendExtensionReceiverOuterCaptureOperation(crossinline f: suspend (T) -> Int): Int = blockOn { f(this@suspendExtensionReceiverOuterCaptureOperation) }

// F1 — the inline fn spliced INSIDE a `suspend fun`, so the payload's __outer-capturing newSuspendLambda is walked by
// SuspendColdLowering's cold transform (GAP 2), which must PRESERVE the 2B __outer capValues override.
suspend fun suspendExtensionReceiverOuterCaptureOperationerationInSuspend(base: Int): Int = base.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 5) }

class SuspendExtensionReceiverCaptureTests {
    @TestAttribute
    fun outerReceiverRebindInPayloadSuspendLambda() {
        assertEquals(42, 20.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 22) })   // f(20) = add(20, 22) = 42
        assertEquals(7, 0.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 7) })      // f(0)  = add(0, 7)   = 7
        assertEquals(20, blockOn { suspendExtensionReceiverOuterCaptureOperationerationInSuspend(15) })      // op spliced in a SUSPEND caller: add(15, 5) = 20
    }
}
