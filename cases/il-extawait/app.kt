// #10 — `await` generalized to the .NET AWAITABLE PATTERN via a GENERIC EXTENSION GetAwaiter (the WinRT
// IAsyncOperation<T> shape, proved without the WinRT projection). `MyOp<T>` (defined in runtime.cs) has NO member
// GetAwaiter; it is awaitable only through `static MyAwaiter<T> GetAwaiter<T>(this MyOp<T>)` in MyOpExtensions.
// facadegen discovers the referenced [Extension] GetAwaiter and injects `suspend fun <T> MyOp<T>.await(): T`;
// bir2cir's EmitAwaitPoint emits `MyOpExtensions.GetAwaiter<Int>(op)` (clrGenericStatic — receiver as arg0, the
// method type arg unified from the concrete `MyOp<Int>`), then the SAME IsCompleted/OnCompleted/GetResult dance.
// Covers BOTH await paths: a synchronously-completed op (IsCompleted true -> fast path) AND one that SUSPENDS then
// resumes on the threadpool (OnCompleted schedules the continuation; blockOn drains it).
import MyLib.MyOp
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun syncAwait(): Int {
    val op = MyOp<Int>(7, true)     // already completed -> IsCompleted true -> inline resume
    return op.await() + 1           // 8
}

suspend fun suspAwait(): Int {
    val op = MyOp<Int>(41, false)   // suspends -> OnCompleted schedules the resume on the threadpool
    return op.await() + 1           // 42
}

fun main() {
    println(blockOn { syncAwait() })   // 8
    println(blockOn { suspAwait() })   // 42
}
