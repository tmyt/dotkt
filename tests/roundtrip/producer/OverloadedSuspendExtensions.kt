package roundtrip.overloadedsuspendextensions

import kotlin.coroutines.*

class PendingJob {
    private var continuation: Continuation<Unit>? = null
    suspend fun join() { suspendCoroutine<Unit> { continuation = it } }
    fun complete() {
        val pending = continuation ?: error("job is not waiting")
        continuation = null
        pending.resume(Unit)
    }
}

class PendingValue<T>(private val value: T) {
    private val job = PendingJob()
    suspend fun await(): T { job.join(); return value }
    fun complete() { job.complete() }
}

suspend fun Collection<PendingJob>.joinSelected(): String {
    for (job in this) job.join()
    return "collection"
}

suspend fun joinSelected(vararg jobs: PendingJob): String {
    for (job in jobs) job.join()
    return "array"
}

suspend fun <T> Collection<PendingValue<T>>.awaitSelected(): List<T> {
    val result = mutableListOf<T>()
    for (value in this) result.add(value.await())
    return result
}

suspend fun <T> awaitSelected(vararg values: PendingValue<T>): List<T> {
    val result = mutableListOf<T>()
    for (i in values.indices.reversed()) result.add(values[i].await())
    return result
}
