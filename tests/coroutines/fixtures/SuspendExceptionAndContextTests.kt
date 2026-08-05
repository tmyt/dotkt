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
// Top-level names distinguish suspending catch, cross-thread resume, and coroutine-context reads under descriptive
// `suspendContext`/`SuspendContext` stems.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Thread
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import dotkt.support.blockOn

// ---- il-suspendcatch -----------------------------------------------------------------------------------------
suspend fun suspendContextCatchMayFail(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the TRY body
    if (x < 0) throw IllegalStateException("neg")
    return x * 2
}

suspend fun suspendContextCatchFallback(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension in the CATCH handler
    return 100 + x
}

suspend fun suspendContextCatchRecover(x: Int): Int =
    try {
        suspendContextCatchMayFail(x)
    } catch (e: IllegalStateException) {
        suspendContextCatchFallback(x)
    }

suspend fun suspendContextCatchClassify(x: Int): Int =
    try {
        if (x == 1) throw IllegalStateException("a")
        if (x == 2) throw IllegalArgumentException("b")
        suspendContextCatchFallback(x)                       // suspends; returns 100 + x
    } catch (e: IllegalStateException) {
        suspendContextCatchFallback(100)                     // suspend handler 1 -> 200
    } catch (e: IllegalArgumentException) {
        suspendContextCatchFallback(200)                     // suspend handler 2 -> 300
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
suspend fun suspendContextCoroutineContextEcho(x: Int): Int = x

suspend fun suspendContextCoroutineContextSmRead(): String {               // SM path: coroutineContext -> this(SM).get_context()
    val c = coroutineContext
    val y = suspendContextCoroutineContextEcho(1)
    return c.toString() + y
}

suspend fun suspendContextCoroutineContextDirectRead(): String = coroutineContext.toString()   // no-SM body-direct: completion.get_context()

class SuspendContextCoroutineContextHolder(val tag: Int) {
    suspend fun member(): String {                   // member SM: `this`=SM (get_context), `$this`=Holder (tag)
        val c = coroutineContext
        return c.toString() + (tag + suspendContextCoroutineContextEcho(1))
    }
}

class SuspendExceptionAndContextTests {
    @TestAttribute
    fun hoistedSuspendingCatch() {
        assertEquals(10, blockOn { suspendContextCatchRecover(5) })      // 10  (mayFail: 5*2)
        assertEquals(99, blockOn { suspendContextCatchRecover(-1) })     // 99  (mayFail throws -> fallback: 100 + (-1))
        assertEquals(103, blockOn { suspendContextCatchClassify(3) })    // 103 (no throw -> fallback: 100 + 3)
        assertEquals(200, blockOn { suspendContextCatchClassify(1) })    // 200 (ISE -> fallback(100))
        assertEquals(300, blockOn { suspendContextCatchClassify(2) })    // 300 (IAE -> fallback(200))
    }

    @TestAttribute
    fun crossThreadResumeCAS() {
        assertEquals(42, blockOn { suspendContextAsyncResume() })   // 42
    }

    @TestAttribute
    fun currentContinuationContextRead() {
        assertEquals("EmptyCoroutineContext1", blockOn { suspendContextCoroutineContextSmRead() })       // EmptyCoroutineContext1
        assertEquals("EmptyCoroutineContext", blockOn { suspendContextCoroutineContextDirectRead() })    // EmptyCoroutineContext
        assertEquals("EmptyCoroutineContext2", blockOn { SuspendContextCoroutineContextHolder(1).member() }) // EmptyCoroutineContext2
    }
}
