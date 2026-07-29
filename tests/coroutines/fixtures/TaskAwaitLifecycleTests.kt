// .NET Task ⇒ Kotlin suspend REVERSE-bridge battery — migrates the `kotlin.clr.await` cases onto the in-process
// NUnit suite. The reference-KLIB-projected `Task.await()` extension (a suspendCall marker) is lowered by bir2cir
// (SuspendColdLowering.EmitAwaitPoint) into the cold-core awaiter dance (GetAwaiter/IsCompleted/GetResult, else
// OnCompleted+return COROUTINE_SUSPENDED). Driven by the shared `dotkt.support.blockOn` harness. Each old case's
// `main` + stdout-golden becomes one @TestAttribute method preserving every asserted value 1:1 (see the
// `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-genasync  -> genasync_genuineAsyncTaskDelay
//                   a suspend fun that TRULY suspends on a real .NET Task (Task.Delay), resumes on the
//                   threadpool, drained by blockOn (the genuine-async isolation rung).
//   il-taskawait -> taskawait_syncFastPath
//                   the SYNC FAST PATH: both awaited tasks are already completed, so IsCompleted is true and no
//                   suspension happens — validates the await marker lowering, the TaskAwaiter STRUCT calls
//                   (generic Task<Int> + non-generic void), and the generic result read-back.
//   il-cofinally -> cofinally_finallyRunsExactlyOnce
//                   (BUG 1) a suspension INSIDE a try whose finally closes a resource: the SM returns
//                   COROUTINE_SUSPENDED from inside the try, so the finally must run EXACTLY ONCE at the real
//                   (post-resume) exit, not early on the `leave` too. A genuine `Task.Delay(1).await()`
//                   suspension is required to reproduce. The former println("close") side effect is captured
//                   into a call counter and asserted == 1 (strictly stronger than the stdout `close` diff).
//
// Top-level names are family-prefixed (`ga`/`ta`/`cof`) so they can't clash with sibling coroutine fixtures or
// the stdlib within this single assembly.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.Task1
import System.Threading.Tasks.TaskCompletionSource1
import dotkt.support.blockOn

// ---- il-genasync ---------------------------------------------------------------------------------------------
suspend fun gaF(): Int {
    Task.Delay(1).await()   // genuine suspension on a real .NET async
    return 7
}

// ---- il-taskawait --------------------------------------------------------------------------------------------
suspend fun taGenAwait(): Int {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(42)
    return tcs.Task.await() + 1        // generic Task<Int>.await(), already-completed -> fast path -> 43
}

suspend fun taUnitAwait(): Int {
    Task.CompletedTask.await()          // non-generic Task.await(): Unit, already-completed -> fast path
    return 7
}

// ---- il-cofinally --------------------------------------------------------------------------------------------
var cofCloseCount = 0

class CofRes {
    fun close() { cofCloseCount++ }     // former println("close") — captured so we can assert it ran ONCE
}

suspend fun cofUseRes(): Int {
    val r = CofRes()
    try {
        Task.Delay(1).await()   // genuine suspension inside the try body
        return 42
    } finally {
        r.close()               // must run exactly ONCE, AFTER the resume (the bug ran it twice)
    }
}

class TaskAwaitLifecycleTests {
    @TestAttribute
    fun genuineAsyncTaskDelay() {
        assertEquals(7, blockOn { gaF() })   // 7
    }

    @TestAttribute
    fun syncFastPath() {
        assertEquals(43, blockOn { taGenAwait() })   // 43
        assertEquals(7, blockOn { taUnitAwait() })   // 7
    }

    @TestAttribute
    fun finallyRunsExactlyOnce() {
        cofCloseCount = 0
        val v = blockOn { cofUseRes() }
        assertEquals(42, v)              // former golden: 42
        assertEquals(1, cofCloseCount)   // former golden: "close" printed exactly once
    }
}
