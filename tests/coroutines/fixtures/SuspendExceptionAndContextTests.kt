// feature fixture — the non-inline-splice SM-core family: suspending try/catch hoist, cross-thread SafeContinuation resume,
// and the current-continuation `coroutineContext` read. (The inline-splice-heavy suspendnestedcapture and the
// inline-forEach suspendloop are each in their OWN file-class — SuspendContextNestedCaptureTests / SuspendContextSuspendLoopTests — because
// bir2cir's inline-splice spilling mis-associates carrier locals when heavy inline cases share a file-class.) Driven by
// the shared `dotkt.support.blockOn` harness; each former case's `main` + golden -> one @TestAttribute method (1:1).
//
// Coverage preserved (old case -> method):
//   il-suspendcatch   -> suspendcatch_hoistedSuspendingCatch   (#78 Defect B: two-level dispatch + multi-catch)
//   il-safecontresume -> safecontresume_crossThreadResumeCAS   (#142: UNDECIDED->RESUMED CAS across threads)
//   il-coroutinectx   -> coroutinectx_currentContinuationContextRead (#79: SM / body-direct / member shapes)
//
// Top-level names carry a per-case token (`scat`/`scr`/`cctx`) under the shared `suspendContext`/`SuspendContext` prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Thread
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import dotkt.support.blockOn

// ---- il-suspendcatch -----------------------------------------------------------------------------------------
suspend fun suspendContextScatMayFail(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the TRY body
    if (x < 0) throw IllegalStateException("neg")
    return x * 2
}

suspend fun suspendContextScatFallback(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the CATCH handler
    return 100 + x
}

suspend fun suspendContextScatRecover(x: Int): Int =
    try {
        suspendContextScatMayFail(x)
    } catch (e: IllegalStateException) {
        suspendContextScatFallback(x)
    }

suspend fun suspendContextScatClassify(x: Int): Int =
    try {
        if (x == 1) throw IllegalStateException("a")
        if (x == 2) throw IllegalArgumentException("b")
        suspendContextScatFallback(x)                       // suspends; returns 100 + x
    } catch (e: IllegalStateException) {
        suspendContextScatFallback(100)                     // suspend handler 1 -> 200
    } catch (e: IllegalArgumentException) {
        suspendContextScatFallback(200)                     // suspend handler 2 -> 300
    }

// ---- il-safecontresume ---------------------------------------------------------------------------------------
suspend fun suspendContextAsyncResume(): Int = suspendCoroutine { cont ->
    val worker = Thread({
        Thread.Sleep(50)
        cont.resume(42)
    })
    worker.Start()
}

// ---- il-coroutinectx -----------------------------------------------------------------------------------------
suspend fun suspendContextCctxEcho(x: Int): Int = x

suspend fun suspendContextCctxSmRead(): String {               // SM path: coroutineContext -> this(SM).get_context()
    val c = coroutineContext
    val y = suspendContextCctxEcho(1)
    return c.toString() + y
}

suspend fun suspendContextCctxDirectRead(): String = coroutineContext.toString()   // no-SM body-direct: completion.get_context()

class SuspendContextCctxHolder(val tag: Int) {
    suspend fun member(): String {                   // member SM: `this`=SM (get_context), `$this`=Holder (tag)
        val c = coroutineContext
        return c.toString() + (tag + suspendContextCctxEcho(1))
    }
}

class SuspendExceptionAndContextTests {
    @TestAttribute
    fun hoistedSuspendingCatch() {
        assertEquals(10, blockOn { suspendContextScatRecover(5) })      // 10  (mayFail: 5*2)
        assertEquals(99, blockOn { suspendContextScatRecover(-1) })     // 99  (mayFail throws -> fallback: 100 + (-1))
        assertEquals(103, blockOn { suspendContextScatClassify(3) })    // 103 (no throw -> fallback: 100 + 3)
        assertEquals(200, blockOn { suspendContextScatClassify(1) })    // 200 (ISE -> fallback(100))
        assertEquals(300, blockOn { suspendContextScatClassify(2) })    // 300 (IAE -> fallback(200))
    }

    @TestAttribute
    fun crossThreadResumeCAS() {
        assertEquals(42, blockOn { suspendContextAsyncResume() })   // 42
    }

    @TestAttribute
    fun currentContinuationContextRead() {
        assertEquals("EmptyCoroutineContext1", blockOn { suspendContextCctxSmRead() })       // EmptyCoroutineContext1
        assertEquals("EmptyCoroutineContext", blockOn { suspendContextCctxDirectRead() })    // EmptyCoroutineContext
        assertEquals("EmptyCoroutineContext2", blockOn { SuspendContextCctxHolder(1).member() }) // EmptyCoroutineContext2
    }
}
