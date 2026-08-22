package suspendnullable

import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

private class NullableSuspendSink : Continuation<Int> {
    var value: Int = 0
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Int>) {
        value = result.getOrThrow()
    }
}

suspend fun nullableSuspendStep(value: Int): Int = value + 1

suspend fun nestedNullableSuspendResult(value: Int): List<Int?> = listOf(null, value)

suspend fun <T> nestedGenericNullableSuspendResult(value: T): List<T?> = listOf(null, value)

class SuspendResultOwner {
    class Nested(val value: Int)
}

suspend fun nestedClassifierSuspendResult(value: Int): List<SuspendResultOwner.Nested?> =
    listOf(null, SuspendResultOwner.Nested(value))

private fun suspendBlock(value: Int): suspend () -> Int =
    { nullableSuspendStep(value) }

fun invokeNullableSuspend(block: (suspend () -> Int)?): Int {
    if (block == null) return -1
    val sink = NullableSuspendSink()
    block.startCoroutine(sink)
    return sink.value
}

fun makeNullableSuspend(value: Int): (suspend () -> Int)? =
    if (value < 0) null else suspendBlock(value)

val nullableTopLevelBlock: (suspend () -> Int)? = suspendBlock(40)
val nullTopLevelBlock: (suspend () -> Int)? = null

class NullableSuspendHolder(value: Int?) {
    val block: (suspend () -> Int)? =
        if (value == null) null else suspendBlock(value)
}
