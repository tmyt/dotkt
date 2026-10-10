package roundtriptests.nominalaliasoverloads

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.nominalaliasoverloads.*

private class Completion : Continuation<Unit> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var done = false
    var failure: Throwable? = null
    override fun resumeWith(result: Result<Unit>) {
        failure = result.exceptionOrNull()
        done = true
    }
}

class NominalAliasOverloadTests {
    @TestAttribute
    fun sameDllCallsKeepTheirSelectedNominalDeclaration() {
        check(localAliasCalls())
    }

    @TestAttribute
    fun ordinaryAndMemberOverloadsSurviveSeparateDlls() {
        val values: Iterable<Int> = listOf(1)
        val sequence = sequenceOf(2)
        check(route(values) == 11 && route(sequence) == 22)
        val host = AliasHost()
        check(host.route(values) == 31 && host.route(sequence) == 42)
    }

    @TestAttribute
    fun genericReturnsPreserveTheOriginalValueIdentity() {
        val values: Iterable<String> = listOf("a")
        val sequence = sequenceOf("b")
        check(values.keepNominal() === values)
        check(sequence.keepNominal() === sequence)
    }

    @TestAttribute
    fun genericForwardingRetainsTheNominalDeclaration() {
        val sequence = sequenceOf("forward")
        check(forwardSequence(sequence) === sequence)
    }

    @TestAttribute
    fun boundCallableReferencesLinkTheSelectedDeclaration() {
        val values: Iterable<String> = listOf("a")
        val sequence = sequenceOf("b")
        val first: () -> Iterable<String> = values::keepNominal
        val second: () -> Sequence<String> = sequence::keepNominal
        check(first() === values && second() === sequence)
    }

    @TestAttribute
    fun unboundCallableReferencesLinkTheSelectedDeclaration() {
        val first: (Iterable<Int>) -> Iterable<Int> = Iterable<Int>::keepNominal
        val second: (Sequence<Int>) -> Sequence<Int> = Sequence<Int>::keepNominal
        val values: Iterable<Int> = listOf(1)
        val sequence = sequenceOf(2)
        check(first(values) === values && second(sequence) === sequence)
    }

    @TestAttribute
    fun suspendNominalOverloadsRemainDistinctAcrossBothResumptions() {
        val pause = Pause()
        val completion = Completion()
        var firstDone = false
        val action: suspend () -> Unit = {
            check(listOf("a").routeWaiting(pause) == "iterable")
            firstDone = true
            check(sequenceOf("b").routeWaiting(pause) == "sequence")
        }
        action.startCoroutine(completion)
        check(!firstDone && !completion.done)
        pause.complete()
        check(firstDone && !completion.done)
        pause.complete()
        check(completion.done)
        completion.failure?.let { throw it }
    }
}
