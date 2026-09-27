package roundtrip.suspendmodifiers

import kotlin.coroutines.*

class ModifierGate {
    private var pending: Continuation<Unit>? = null
    suspend fun pause() { suspendCoroutine<Unit> { pending = it } }
    fun release() { pending!!.resume(Unit) }
}

interface SuspendOperations {
    suspend operator fun invoke(value: String): String
    suspend infix fun join(value: String): String
}

class ConcreteOperations(private val gate: ModifierGate) : SuspendOperations {
    override suspend fun invoke(value: String): String { gate.pause(); return value }
    override suspend fun join(value: String): String { gate.pause(); return value }
    suspend operator fun plus(value: String): String { gate.pause(); return value }
}

suspend infix fun String.appendSuspending(other: String): String = this + other

suspend fun sameModuleModifiers(gate: ModifierGate): String {
    val concrete = ConcreteOperations(gate)
    val abstract: SuspendOperations = concrete
    return abstract("a") + (abstract join "b") + (concrete + "c")
}
