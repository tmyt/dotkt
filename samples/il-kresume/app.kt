// A2 — the resume API from Kotlin: a leaf stores its continuation and is resumed via cont.resume(v) /
// cont.resumeWith(Result.success(v)) inside a Task-completion callback (resume happens in Kotlin, not C#).
import clr.Api2
import clr.Coro
import clr.Task
import clr.Bridge.onCompleteCb
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

// resume via the `resume` extension.
suspend fun awaitViaResume(task: Task<Int>): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onCompleteCb(task) { v -> c.resume(v) }
    COROUTINE_SUSPENDED
}

// resume via resumeWith(Result.success(...)).
suspend fun awaitViaResumeWith(task: Task<Int>): Int = suspendCoroutineUninterceptedOrReturn { c ->
    onCompleteCb(task) { v -> c.resumeWith(Result.success(v + 100)) }
    COROUTINE_SUSPENDED
}

fun main() {
    println(Coro.run { awaitViaResume(Api2.step(5)) })       // 5
    println(Coro.run { awaitViaResumeWith(Api2.step(7)) })   // 107
}
