package suspenddefaultphysicalbound

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private class Owner<A : CharSequence, T>(val label: A, val value: T) {
    suspend inline fun <reified R> matches(
        action: () -> Unit, item: Any?,
        noinline block: suspend () -> Pair<T, Boolean> = {
            Pair(value, label.length > 0 && item is R)
        },
    ): Pair<T, Boolean> { action(); return block() }
}

private suspend inline fun <A : CharSequence, reified R> methodBound(
    label: A, item: Any?,
    noinline block: suspend () -> Boolean = { label.length > 0 && item is R },
): Boolean = block()

private class Gate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() = suspendCoroutine<Unit> { pending = it }
    fun release() { val next = pending!!; pending = null; next.resume(Unit) }
}

private fun <A : CharSequence> deferred(label: A, gate: Gate): suspend () -> Int = {
    gate.pause()
    label.length
}

private class Completion<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var result: Result<T>? = null
    override fun resumeWith(result: Result<T>) { this.result = result }
}

private fun complete(block: suspend () -> Unit) {
    val completion = Completion<Unit>()
    block.startCoroutine(completion)
    check(completion.result != null)
    completion.result!!.getOrThrow()
}

class SuspendDefaultPhysicalBoundTests {
    @TestAttribute
    fun ownerBoundsFollowThePhysicalDeclaration() = complete {
        var effects = 0
        check(Owner("label", 17).matches<String>({ effects++ }, "text") == Pair(17, true))
        check(Owner("label", "owner").matches<Int>({ effects++ }, "text") == Pair("owner", false))
        check(Owner("label", 23).matches<String?>({ effects++ }, null) == Pair(23, true))
        check(Owner("label", "nullable").matches<Int>({ effects++ }, null) == Pair("nullable", false))
        check(effects == 4)
    }

    @TestAttribute
    fun methodBoundsUseTheSameRepresentation() = complete {
        check(methodBound<String, String?>("label", null))
        check(!methodBound<String, Int>("label", null))
        check(!methodBound<String, String>("", "text"))
    }

    @TestAttribute
    fun ordinaryCapturedBoundsSurviveSuspension() {
        val gate = Gate()
        val completion = Completion<Int>()
        deferred("label", gate).startCoroutine(completion)
        check(completion.result == null)
        gate.release()
        check(completion.result!!.getOrThrow() == 5)
    }
}
