package roundtrip.suspendwitness

import kotlin.coroutines.*

suspend inline fun <A, reified T> importedTypeCheck(unused: A, item: Any?): Boolean = item is T

suspend inline fun <A, reified T> witnessFreeCheck(unused: A): Boolean = true

suspend inline fun <A, reified T> Any?.importedExtensionCheck(unused: A): Boolean = this is T

class WitnessOwner<O>(val value: O) {
    suspend inline fun <A, reified T> matches(unused: A, item: Any?): Boolean = item is T
}

class WitnessGate {
    private var continuation: Continuation<Unit>? = null
    suspend fun pause(): Unit = suspendCoroutine { continuation = it }
    fun release() {
        val saved = continuation!!
        continuation = null
        saved.resume(Unit)
    }
}

suspend inline fun <A, reified T> delayedTypeCheck(unused: A, item: Any?, gate: WitnessGate): Boolean {
    gate.pause()
    return item is T
}

suspend fun sameModuleWitnessChecks(): Boolean =
    importedTypeCheck<Int, String>(0, "text") &&
        importedTypeCheck<Int, String?>(0, null) &&
        !importedTypeCheck<Int, String>(0, null)
