package roundtriptests.defaultwitness

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.defaultwitness.*
import roundtrip.suspenddefaultframes.SuspendDefaultGate

private suspend inline fun <reified T> selectForward(item: Any): T? = selectDefault<T>(item)
private suspend inline fun <reified X, reified T> secondForward(item: Any?): Boolean = matchesDefault<T>(item)
private suspend inline fun <reified T> nestedForward(item: Any?): Boolean = nestedDefault<T>(item)
private inline fun <reified T> ordinaryForward(item: Any?): Boolean = ordinaryDefault<T>(item)
private inline fun <reified T> transitiveForward(item: Any?): Boolean = ordinaryForward<T>(item)
private inline fun <reified T> nullForward(): Boolean = nullDefault<T>()
private inline fun <reified T> inlineMixedForward(): Boolean = inlineDefault<T>({})
private inline fun <reified T> inlineExplicitForward(): Boolean = inlineDefault<T>({}, false)
private inline fun <reified T> inlineExtensionForward(item: Any?): Boolean = item.inlineExtensionDefault<T>({})
private inline fun <reified T> inlineLiftedForward(): Boolean = inlineLiftedDefault<T>({})
private inline fun <reified T> localInlineDefault(action: () -> Unit, matches: Boolean = null is T): Boolean {
    action()
    return matches
}
private inline fun <reified T> localInlineForward(): Boolean = localInlineDefault<T>({})
private suspend inline fun <reified T> explicitForward(item: Any?): Boolean = matchesDefault<T>(item) { true }
private suspend inline fun <reified T> delayedForward(item: Any?, gate: SuspendDefaultGate): Boolean =
    delayedDefault<T>(item, gate)

private class Completion : Continuation<Any?> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var done = false
    var value: Any? = null
    var failure: Throwable? = null
    override fun resumeWith(result: Result<Any?>) {
        failure = result.exceptionOrNull()
        if (failure == null) value = result.getOrThrow()
        done = true
    }
}

private fun start(block: suspend () -> Any?): Completion {
    val completion = Completion()
    block.startCoroutine(completion)
    return completion
}

private fun completed(expected: Any?, block: suspend () -> Any?) {
    val completion = start(block)
    check(completion.done)
    completion.failure?.let { throw it }
    check(completion.value == expected) { "expected $expected, got ${completion.value}" }
}

class DefaultWitnessDemandTests {
    @TestAttribute
    fun importedDefaultsContributeToTheirOmittingCallers() {
        completed(67) { selectForward<Int?>(67) }
        completed(71) { selectForward<Int>(71) }
        completed("value") { selectForward<String>("value") }
        completed(null) { selectForward<String>(17) }
        completed(true) { secondForward<Int, String?>(null) }
        completed(false) { secondForward<Int, String>(null) }
        completed(true) { secondForward<String, Int?>(null) }
        completed(false) { secondForward<String, Int>(null) }
    }

    @TestAttribute
    fun nestedOrdinaryAndExplicitDefaultsRetainTheirContracts() {
        completed(true) { nestedForward<String?>(null) }
        completed(false) { nestedForward<String>(null) }
        check(ordinaryForward<String?>(null))
        check(!ordinaryForward<String>(null))
        check(transitiveForward<String?>(null))
        check(!transitiveForward<String>(null))
        check(nullForward<String?>())
        check(!nullForward<String>())
        check(inlineMixedForward<String?>())
        check(!inlineMixedForward<String>())
        check(!inlineExplicitForward<String?>())
        check(inlineExtensionForward<String?>(null))
        check(!inlineExtensionForward<String>(null))
        check(inlineLiftedForward<String?>())
        check(!inlineLiftedForward<String>())
        check(localInlineForward<String?>())
        check(!localInlineForward<String>())
        completed(true) { explicitForward<String>(null) }
    }

    @TestAttribute
    fun defaultWitnessSurvivesActualSuspension() {
        val gate = SuspendDefaultGate()
        val completion = start { delayedForward<String?>(null, gate) }
        check(!completion.done)
        check(gate.entries == 1)
        gate.release()
        check(completion.done)
        completion.failure?.let { throw it }
        check(completion.value == true)
    }
}
