import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspendwitness.*

private class WitnessCompletion : Continuation<Boolean> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var done = false
    var value = false
    var failure: Throwable? = null
    override fun resumeWith(result: Result<Boolean>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        done = true
    }
}

private fun checkWitness(expected: Boolean, block: suspend () -> Boolean) {
    val completion = WitnessCompletion()
    block.startCoroutine(completion)
    check(completion.done)
    completion.failure?.let { throw it }
    check(completion.value == expected)
}

private suspend inline fun <A, reified T> forwardWitness(unused: A, item: Any?): Boolean =
    importedTypeCheck<A, T>(unused, item)

class SuspendWitnessMetadataTests {
    @TestAttribute
    fun importedSuspendWitnessesAreNotSourceArguments() {
        checkWitness(true) { sameModuleWitnessChecks() }
        checkWitness(true) { witnessFreeCheck<Int, String?>(0) }
        checkWitness(true) { importedTypeCheck<Int, String>(0, "text") }
        checkWitness(false) { importedTypeCheck<Int, String>(0, null) }
        checkWitness(true) { importedTypeCheck<Int, String?>(0, null) }
        checkWitness(true) { importedTypeCheck<String, Int>("unused", 17) }
        checkWitness(false) { importedTypeCheck<String, Int>("unused", null) }
        checkWitness(true) { importedTypeCheck<String, Int?>("unused", null) }
        checkWitness(false) { importedTypeCheck<String, Int>("unused", "text") }
        checkWitness(true) { forwardWitness<Int, String?>(0, null) }
        checkWitness(false) { forwardWitness<Int, String>(0, null) }
        checkWitness(true) { (null as Any?).importedExtensionCheck<Int, String?>(0) }
        checkWitness(false) { (null as Any?).importedExtensionCheck<Int, String>(0) }
        val owner = WitnessOwner("owner")
        checkWitness(true) { owner.matches<Int, String?>(0, null) }
        checkWitness(false) { owner.matches<Int, String>(0, null) }
    }

    @TestAttribute
    fun importedWitnessSurvivesSuspension() {
        val gate = WitnessGate()
        val completion = WitnessCompletion()
        val block: suspend () -> Boolean = { delayedTypeCheck<Int, String?>(0, null, gate) }
        block.startCoroutine(completion)
        check(!completion.done)
        gate.release()
        check(completion.done)
        completion.failure?.let { throw it }
        check(completion.value)
    }
}
