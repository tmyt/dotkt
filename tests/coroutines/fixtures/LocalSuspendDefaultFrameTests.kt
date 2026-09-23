package localsuspenddefaultframe

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private suspend inline fun <reified T> localDefaultMatches(
    item: Any?,
    noinline block: suspend () -> Boolean = { item is T },
): Boolean = block()

private suspend inline fun <reified X, reified T> localDefaultForward(item: Any?): Boolean =
    localDefaultMatches<T>(item)

private fun expectCompleted(expected: Boolean, block: suspend () -> Boolean) {
    var done = false
    var value = false
    var failure: Throwable? = null
    block.startCoroutine(object : Continuation<Boolean> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<Boolean>) {
            failure = result.exceptionOrNull()
            if (failure == null) value = result.getOrThrow()
            done = true
        }
    })
    check(done)
    failure?.let { throw it }
    check(value == expected)
}

class LocalSuspendDefaultFrameTests {
    @TestAttribute
    fun defaultLambdaUsesItsCallersReorderedFrame() {
        expectCompleted(true) { localDefaultForward<Int, String?>(null) }
        expectCompleted(false) { localDefaultForward<Int, String>(null) }
        expectCompleted(true) { localDefaultForward<String, Int>(17) }
        expectCompleted(false) { localDefaultForward<Int, String>(17) }
    }
}
