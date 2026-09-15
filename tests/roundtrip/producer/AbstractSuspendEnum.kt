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

interface EnumReader { suspend fun read(): String }
enum class InheritedSuspendReader : EnumReader {
    FIRST { override suspend fun read(): String = "first" },
    SECOND { override suspend fun read(): String = "second" }
}

enum class BoundedSuspendEcho {
    VALUE {
        override suspend fun <T : Comparable<T>> read(gate: EnumGate<T>): T = gate.await()
    };
    abstract suspend fun <T : Comparable<T>> read(gate: EnumGate<T>): T
}
