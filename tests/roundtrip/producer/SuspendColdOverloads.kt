package roundtrip.suspendcoldoverloads

import kotlin.coroutines.Continuation
import kotlin.coroutines.suspendCoroutine

class SelectionGate {
    var pending: Continuation<String>? = null
    var trace: String = ""

    suspend fun pause(label: String): String = suspendCoroutine {
        trace += label
        pending = it
    }
}

suspend fun Collection<String>.selectCold(gate: SelectionGate): String {
    val value = gate.pause("collection")
    return "C:$value:$size"
}

suspend fun Array<String>.selectCold(gate: SelectionGate): String {
    val value = gate.pause("array")
    return "A:$value:$size"
}

suspend fun <T> Collection<T>.selectGenericCold(gate: SelectionGate): T {
    gate.pause("generic-collection")
    return first()
}

suspend fun <T> Array<T>.selectGenericCold(gate: SelectionGate): T {
    gate.pause("generic-array")
    return first()
}
