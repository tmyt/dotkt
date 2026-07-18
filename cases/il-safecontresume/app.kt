// #142 regression guard. suspendCoroutine builds a SafeContinuation; its state field transitions
// UNDECIDED -> COROUTINE_SUSPENDED (getOrThrow, on the suspending frame) and COROUTINE_SUSPENDED -> RESUMED
// (resumeWith, on the resuming thread). Here the block does NOT resume synchronously — it hands the continuation
// to a WORKER thread that resumes after a delay, so the two transitions genuinely happen on different threads
// (the race the fix's Interlocked.CompareExchange CAS over the @Volatile field closes). blockOn drives the cold
// core and blocks until completion; 42 is only observable if the async resume lands correctly through the CAS.
import System.Threading.Thread
import kotlin.coroutines.suspendCoroutine
import kotlin.coroutines.resume
import dotkt.support.blockOn

suspend fun asyncResume(): Int = suspendCoroutine { cont ->
    val worker = Thread({
        Thread.Sleep(50)
        cont.resume(42)
    })
    worker.Start()
}

fun main() {
    println(blockOn { asyncResume() })   // 42
}
