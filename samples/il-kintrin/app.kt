// Phase 2 — the RAW coroutine intrinsic: `suspendCoroutineUninterceptedOrReturn { c -> ... }` hands the coroutine
// its OWN continuation (c) to arbitrary code, returning a value (resume now) or COROUTINE_SUSPENDED (suspend).
// This is exactly what kotlinx-coroutines-core is built on. await() is now written IN KOTLIN via the intrinsic.
import clr.Api2
import clr.Task
import clr.Coro
import clr.KCont
import clr.CoBridge.onComplete
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

// A real suspension built from the intrinsic: pass `c` to onComplete (which resumes it when the Task finishes),
// then return COROUTINE_SUSPENDED to actually suspend.
@KCont suspend fun awaitInt(task: Task<Int>): Int {
    return suspendCoroutineUninterceptedOrReturn { c ->
        onComplete(task, c)
        COROUTINE_SUSPENDED
    }
}

// A synchronously-completing intrinsic leaf (returns a value, never suspends) — proves the value-or-suspend branch.
@KCont suspend fun immediate(): Int {
    return suspendCoroutineUninterceptedOrReturn { c -> 42 }
}

// Compose: a suspend fun that suspends twice via the intrinsic-based awaitInt, plus a sync leaf.
@KCont suspend fun chainViaIntrinsic(): Int {
    val a = awaitInt(Api2.step(10))
    val b = awaitInt(Api2.step(20))
    val c = immediate()
    return a + b + c                 // 10 + 20 + 42 = 72
}

fun main() {
    println(Coro.run { awaitInt(Api2.step(7)) })   // 7
    println(Coro.run { immediate() })               // 42
    println(Coro.run { chainViaIntrinsic() })       // 72
}
