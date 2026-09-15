package roundtrip.abstractsuspendenum

import kotlin.coroutines.*

class EnumGate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
}

enum class SuspendAction {
    VALUE { override suspend fun run() {} };
    abstract suspend fun run()
}

enum class SuspendEcho {
    VALUE { override suspend fun <T> read(gate: EnumGate<T>): T = gate.await() };
    abstract suspend fun <T> read(gate: EnumGate<T>): T
}
