// feature fixture — il-inlsuspendouter: an EXTENSION `inline fun T.op` whose body builds a SOURCE `newSuspendLambda`
// (the `blockOn { … }` arg) capturing `this@op`. Its positional capValue is evaluated in the enclosing payload frame
// and rebound from `__self` to the splice receiver without inferring receiver role from the capture name. All top-level decls
// use the `suspendExtensionReceiverOuterCapture`/`SuspendExtensionReceiverOuterCapture` stem so their simple names are UNIQUE across this
// assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). The former `main` +
// golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun suspendExtensionReceiverOuterCaptureAdd(a: Int, b: Int): Int = a + b

// extension inline fun; the payload's `blockOn { … }` lambda (a SOURCE newSuspendLambda) captures `this@suspendExtensionReceiverOuterCaptureOperation`
// (= __outer, the extension receiver) alongside the crossinline suspend carrier `f`.
inline fun <T> T.suspendExtensionReceiverOuterCaptureOperation(crossinline f: suspend (T) -> Int): Int = blockOn { f(this@suspendExtensionReceiverOuterCaptureOperation) }

// F1 — the inline fn spliced INSIDE a `suspend fun`, so the payload's receiver-capturing newSuspendLambda is walked by
// SuspendColdLowering's cold transform (GAP 2), which must preserve its rewritten positional capValue.
suspend fun suspendExtensionReceiverOuterCaptureOperationInSuspend(base: Int): Int = base.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 5) }

// #563: a plain member extension can contribute two distinct captured `<this>` declarations to a suspend lambda:
// the host dispatch receiver and the extension receiver. Their state-machine fields and body reads must remain 1:1.
class SuspendExtensionReceiverDualCaptureHost {
    var hits: Int = 0

    fun <T> Array<T>.suspendExtensionReceiverDualCaptureGeneric(): suspend () -> T = {
        this@SuspendExtensionReceiverDualCaptureHost.hits++
        this@suspendExtensionReceiverDualCaptureGeneric[0]
    }

    fun IntArray.suspendExtensionReceiverDualCaptureSpecialized(): suspend () -> Int = {
        this@SuspendExtensionReceiverDualCaptureHost.hits++
        this@suspendExtensionReceiverDualCaptureSpecialized[0]
    }

    inline fun <T> Array<T>.suspendExtensionReceiverDualCaptureInlineGeneric(
        crossinline map: (T) -> T,
    ): suspend () -> T = {
        val value = this@suspendExtensionReceiverDualCaptureInlineGeneric[0]
        this@SuspendExtensionReceiverDualCaptureHost.hits++
        map(value)
    }

    inline fun IntArray.suspendExtensionReceiverDualCaptureInlineSpecialized(
        crossinline map: (Int) -> Int,
    ): suspend () -> Int = {
        val value = this@suspendExtensionReceiverDualCaptureInlineSpecialized[0]
        Task.Delay(1).await()
        this@SuspendExtensionReceiverDualCaptureHost.hits++
        map(value)
    }

    suspend fun suspendExtensionReceiverDualCaptureCold(
        receiver: IntArray,
    ): suspend () -> Int {
        Task.Delay(1).await()
        return receiver.suspendExtensionReceiverDualCaptureInlineSpecialized { it + 3 }
    }
}

class SuspendExtensionReceiverCaptureTests {
    @TestAttribute
    fun outerReceiverRebindInPayloadSuspendLambda() {
        assertEquals(42, 20.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 22) })   // f(20) = add(20, 22) = 42
        assertEquals(7, 0.suspendExtensionReceiverOuterCaptureOperation { suspendExtensionReceiverOuterCaptureAdd(it, 7) })      // f(0)  = add(0, 7)   = 7
        assertEquals(20, blockOn { suspendExtensionReceiverOuterCaptureOperationInSuspend(15) })      // op spliced in a SUSPEND caller: add(15, 5) = 20
        val host = SuspendExtensionReceiverDualCaptureHost()
        val generic = with(host) { arrayOf("generic").suspendExtensionReceiverDualCaptureGeneric() }
        val specialized = with(host) { intArrayOf(23).suspendExtensionReceiverDualCaptureSpecialized() }
        assertEquals("generic", blockOn { generic() })
        assertEquals(23, blockOn { specialized() })
        val genericInline = with(host) {
            arrayOf("inline").suspendExtensionReceiverDualCaptureInlineGeneric { it + "!" }
        }
        val specializedInline = with(host) {
            intArrayOf(40).suspendExtensionReceiverDualCaptureInlineSpecialized { it + 2 }
        }
        assertEquals("inline!", blockOn { genericInline() })
        assertEquals(42, blockOn { specializedInline() })
        val cold = blockOn { host.suspendExtensionReceiverDualCaptureCold(intArrayOf(50)) }
        assertEquals(53, blockOn { cold() })
        assertEquals(5, host.hits)
    }
}
