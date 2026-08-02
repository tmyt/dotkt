// .NET Task await / ConfigureAwait / cross-suspension-exception battery (feature fixture). The reference-KLIB-projected
// `Task.await()` extension is lowered by bir2cir (SuspendColdLowering.EmitAwaitPoint) into the cold-core awaiter
// dance; `captureContext = false` selects the ConfiguredTaskAwaitable awaiter. Driven by the shared
// `dotkt.support.blockOn` harness. Each old `main` + stdout golden becomes one @TestAttribute method (1:1 values).
//
// Coverage preserved (old case -> method):
//   il-cfgawait    -> cfgAwait_configureAwaitFalseNonGeneric   (#3: void ConfiguredTaskAwaiter, sync fast path)
//   il-cfgawaitgen -> cfgAwaitGen_configureAwaitFalseGeneric   (#3: generic ConfiguredTaskAwaitable`1 backtick arity)
//   il-cobuild     -> coBuild_realTaskDelayAwait               (P4: two genuine Task.Delay().await() suspensions)
//   il-coexc       -> coExc_exceptionAcrossSuspendBoundary     (throw after resume / nested frame / faulted Task rethrow)
//
// Top-level names are family-prefixed (`configureAwaitNonGeneric`/`configureAwaitGeneric`/`configureAwaitTaskDelay`/`configureAwaitException`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import System.Threading.Tasks.TaskCompletionSource1
import System.InvalidOperationException
import dotkt.support.blockOn

// ---- il-cfgawait: `await(captureContext = false)` on a non-generic already-completed Task -------------------
suspend fun configureAwaitNonGenericAwait(): Int {
    Task.CompletedTask.await(captureContext = false)   // ConfigureAwait(false) awaiter, already-completed -> fast path
    return 5
}

// ---- il-cfgawaitgen: generic `Task<T>.await(captureContext = false)` (the backtick-arity awaiter) ------------
suspend fun configureAwaitGenericAwait(): Int {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(9)
    return tcs.Task.await(captureContext = false) + 1   // generic ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter
}

// ---- il-cobuild: real .NET async suspensions over the P4 await lowering -------------------------------------
suspend fun configureAwaitTaskDelayCompute(n: Int): Int {
    Task.Delay(1).await()   // real .NET async suspension
    return n * n
}
suspend fun configureAwaitTaskDelayTotal(): Int {
    val a = configureAwaitTaskDelayCompute(3)   // 9
    val b = configureAwaitTaskDelayCompute(4)   // 16
    return a + b                  // 25
}

// ---- il-coexc: an exception thrown ACROSS a suspended Task boundary propagates to the caller ----------------
suspend fun configureAwaitExceptionThrowsAfterAwait(): Int {
    Task.Delay(1).await()                 // genuine suspension: resumes on the threadpool
    throw IllegalStateException("boom")   // thrown AFTER the resume — must cross the bridge intact
}
suspend fun configureAwaitExceptionInner(): Int {
    Task.Delay(1).await()
    throw IllegalStateException("nested")
}
suspend fun configureAwaitExceptionOuter(): Int {
    val x = configureAwaitExceptionInner()                // cold call across a suspending frame; fault propagates up
    return x + 1
}
suspend fun configureAwaitExceptionAwaitsFaultedTask(): Int {
    val faulted: Task = Task.FromException(InvalidOperationException("faulted"))
    faulted.await()                       // await must RETHROW the .NET task's fault at GetResult
    return 99
}

class ConfigureAwaitAndExceptionTests {
    @TestAttribute
    fun configureAwaitFalseNonGeneric() {
        assertEquals(5, blockOn { configureAwaitNonGenericAwait() })   // 5
    }

    @TestAttribute
    fun configureAwaitFalseGeneric() {
        assertEquals(10, blockOn { configureAwaitGenericAwait() })   // 10
    }

    @TestAttribute
    fun realTaskDelayAwait() {
        assertEquals(25, blockOn { configureAwaitTaskDelayTotal() })   // 25
    }

    @TestAttribute
    fun exceptionAcrossSuspendBoundary() {
        var m1: String? = null
        try { blockOn { configureAwaitExceptionThrowsAfterAwait() } } catch (e: IllegalStateException) { m1 = e.message }
        assertEquals("boom", m1)      // former golden: "caught: boom"

        var m2: String? = null
        try { blockOn { configureAwaitExceptionOuter() } } catch (e: IllegalStateException) { m2 = e.message }
        assertEquals("nested", m2)    // former golden: "caught2: nested"

        var m3: String? = null
        try { blockOn { configureAwaitExceptionAwaitsFaultedTask() } } catch (e: Throwable) { m3 = e.message }
        assertEquals("faulted", m3)   // former golden: "caught3: faulted"
    }
}
