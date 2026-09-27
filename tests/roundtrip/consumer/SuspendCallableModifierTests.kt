package roundtriptests.suspendmodifiers

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.suspendmodifiers.*

private class Completion : Continuation<String> {
    var done = false
    var value: String? = null
    var failure: Throwable? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<String>) {
        failure = result.exceptionOrNull()
        value = result.getOrNull()
        done = true
    }
}

private fun verifySuspensions(count: Int, expected: String, block: suspend (ModifierGate) -> String) {
    val gate = ModifierGate()
    val completion = Completion()
    val run: suspend () -> String = { block(gate) }
    run.startCoroutine(completion)
    repeat(count) {
        check(!completion.done)
        gate.release()
    }
    check(completion.done)
    completion.failure?.let { throw it }
    check(completion.value == expected)
}

class SuspendCallableModifierTests {
    @TestAttribute
    fun importedConcreteAndAbstractModifiersSurviveSuspension() {
        verifySuspensions(5, "abcde") { gate ->
            val concrete = ConcreteOperations(gate)
            val abstract: SuspendOperations = concrete
            concrete("a") + (concrete join "b") + (concrete + "c") +
                abstract("d") + (abstract join "e")
        }
    }

    @TestAttribute
    fun sameModuleAndImportedExtensionModifiersRemainAvailable() {
        verifySuspensions(3, "abc") { gate -> sameModuleModifiers(gate) }
        verifySuspensions(0, "xy") { _ -> "x" appendSuspending "y" }
    }
}
