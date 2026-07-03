// kotlin.clr cold-core coroutine surface — the HONEST .NET-async form (bundle-6 P4): a real
// `Task.Delay(1).await()` suspension (bir2cir lowers `Task.await()` to the TaskAwaiter + Continuation
// bridge, SuspendColdLowering.EmitAwaitPoint) rather than the retired `kotlin.clr.delay` crutch.
// `blockOn { total() }` drives the cold Continuation core to completion and drains the root sink.
import System.Threading.Tasks.Task
import kotlin.clr.await
import kotlin.clr.blockOn

suspend fun compute(n: Int): Int {
    Task.Delay(1).await()   // real .NET async suspension over the P4 await lowering
    return n * n
}

suspend fun total(): Int {
    val a = compute(3)      // direct Kotlin→Kotlin suspend call (cold entry)
    val b = compute(4)
    return a + b            // 9 + 16
}

fun main() {
    println(blockOn { total() })   // 25
}
