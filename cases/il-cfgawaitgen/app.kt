// #3: GENERIC `Task<T>.await(captureContext = false)`. The generic ConfigureAwait(false) awaiter is the NESTED struct
// `ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter` — the generic arity `1 rides the OUTER type, so its FQN already
// carries a backtick. ilemit's ConstructGeneric must NOT append a SECOND `1 (that yields `...ConfiguredTaskAwaiter`1`,
// which ResolveType can't find). Companion of il-cfgawait (the NON-generic ConfiguredTaskAwaitable+ConfiguredTaskAwaiter
// path — void result) and il-taskawait (the generic Task<Int>.await() DEFAULT-capturing awaiter). SYNC FAST PATH.
import System.Threading.Tasks.TaskCompletionSource1
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun cfgAwaitGen(): Int {
    val tcs = TaskCompletionSource1<Int>()
    tcs.SetResult(9)
    return tcs.Task.await(captureContext = false) + 1   // generic Task<Int>.await(false) -> ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter
}

fun main() {
    println(blockOn { cfgAwaitGen() })   // 10
}
