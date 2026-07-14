// #10 — `await` generalized to the .NET AWAITABLE PATTERN (GetAwaiter), beyond Task. This case proves the
// generalization for a NON-Task BCL awaitable: `System.Threading.Tasks.ValueTask<T>`, which has a MEMBER
// `GetAwaiter()` returning `ValueTaskAwaiter<T>` (IsCompleted / GetResult / INotifyCompletion) — no `.AsTask()`
// needed. facadegen detects the pattern and injects `suspend fun <T> ValueTask1<T>.await(): T`; bir2cir's
// EmitAwaitPoint discovers the ValueTaskAwaiter shape from ref metadata and emits the SAME awaiter dance it emits
// for Task — zero per-type hardcode. SYNC FAST PATH: a value-constructed ValueTask is already completed, so
// IsCompleted is true and the coroutine resumes inline through GetResult. (The genuine async suspend+resume path is
// exercised for a non-Task extension awaitable by il-extawait, and for Task by il-cobuild.)
import System.Threading.Tasks.ValueTask1
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun vtAwait(): Int {
    val vt = ValueTask1<Int>(41)   // a synchronously-completed ValueTask<Int>
    return vt.await() + 1          // 42 — ValueTaskAwaiter<Int> fast path (IsCompleted true)
}

fun main() {
    println(blockOn { vtAwait() })   // 42
}
