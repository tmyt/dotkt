// Coverage (POLISH Wave-2 family 6, item 1): an exception thrown ACROSS a suspended Task boundary must
// propagate through the cold-core / Task bridge to the caller. Three paths:
//  (a) a suspend fun throws AFTER a genuine Task.Delay().await() suspension — the throw happens post-resume
//      (on the threadpool), the SM's invokeSuspend fails, the root Continuation (blockOn's BlockOnSink)
//      captures it via resumeWith(Result.failure) and blockOn RETHROWS it raw into the caller's try/catch.
//  (b) the throw crosses a NESTED suspend frame: inner() suspends then throws, outer() awaits inner() and
//      the fault propagates up the resumeWithException chain to blockOn.
//  (c) awaiting a genuinely FAULTED .NET Task rethrows its fault at await's GetAwaiter().GetResult()
//      (SuspendColdLowering GetResult path) — the fault comes from .NET, not from the cold body.
import System.Threading.Tasks.Task
import System.InvalidOperationException
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun throwsAfterAwait(): Int {
    Task.Delay(1).await()                 // genuine suspension: resumes on the threadpool
    throw IllegalStateException("boom")   // thrown AFTER the resume — must cross the bridge intact
}

suspend fun inner(): Int {
    Task.Delay(1).await()
    throw IllegalStateException("nested")
}
suspend fun outer(): Int {
    val x = inner()                       // cold call across a suspending frame; fault propagates up
    return x + 1
}

suspend fun awaitsFaultedTask(): Int {
    val faulted: Task = Task.FromException(InvalidOperationException("faulted"))
    faulted.await()                       // await must RETHROW the .NET task's fault at GetResult
    return 99
}

fun main() {
    try {
        blockOn { throwsAfterAwait() }
        println("no throw")
    } catch (e: IllegalStateException) {
        println("caught: " + e.message)   // caught: boom
    }

    try {
        blockOn { outer() }
        println("no throw2")
    } catch (e: IllegalStateException) {
        println("caught2: " + e.message)  // caught2: nested
    }

    try {
        blockOn { awaitsFaultedTask() }
        println("no throw3")
    } catch (e: Throwable) {
        println("caught3: " + e.message)  // caught3: faulted
    }

    println("done")
}
