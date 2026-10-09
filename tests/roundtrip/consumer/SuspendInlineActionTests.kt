package roundtriptests.suspendinlineactions

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import kotlin.coroutines.cancellation.CancellationException
import roundtrip.suspendinlineactions.*

private class ActionCompletion : Continuation<Any?> {
    var outcome: Result<Any?>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<Any?>) { outcome = result }
}

private fun start(action: suspend () -> Any?): ActionCompletion {
    val completion = ActionCompletion()
    action.startCoroutine(completion)
    return completion
}

private fun checkReleased(guard: ActionGuard) {
    check(!guard.locked && guard.heldOwner == null)
    check(guard.entries == 1 && guard.exits == 1)
}

private suspend fun returnFromAction(guard: ActionGuard, gate: ActionGate): String {
    guard.withGuard {
        gate.await()
        return "non-local"
    }
}

private suspend fun <T> genericAction(guard: ActionGuard, gate: ActionGate, value: T): T =
    guard.withGuard { gate.await(); value }

class SuspendInlineActionTests {
    @TestAttribute
    fun defaultAndExplicitOwnerRemainHeldAcrossSuspension() {
        val defaultGuard = ActionGuard()
        val defaultGate = ActionGate()
        val defaultCompletion = start { defaultGuard.withGuard { defaultGate.await(); 42 } }
        check(defaultCompletion.outcome == null && defaultGuard.locked)
        check(defaultGuard.heldOwner == null && defaultGuard.exits == 0)
        defaultGate.release()
        check(defaultCompletion.outcome!!.getOrThrow() == 42)
        checkReleased(defaultGuard)

        val owner = Any()
        val explicitGuard = ActionGuard()
        val explicitGate = ActionGate()
        val explicitCompletion = start {
            explicitGuard.withGuard(owner) { explicitGate.await(); "explicit" }
        }
        check(explicitCompletion.outcome == null && explicitGuard.locked)
        check(explicitGuard.heldOwner === owner && explicitGuard.exits == 0)
        explicitGate.release()
        check(explicitCompletion.outcome!!.getOrThrow() == "explicit")
        checkReleased(explicitGuard)

        val entryGate = ActionGate()
        val actionGate = ActionGate()
        val delayedGuard = ActionGuard(entryGate)
        val delayedCompletion = start { delayedGuard.withGuard { actionGate.await(); "two suspensions" } }
        check(delayedCompletion.outcome == null && !delayedGuard.locked && delayedGuard.entries == 0)
        entryGate.release()
        check(delayedCompletion.outcome == null && delayedGuard.locked && delayedGuard.exits == 0)
        actionGate.release()
        check(delayedCompletion.outcome!!.getOrThrow() == "two suspensions")
        checkReleased(delayedGuard)
    }

    @TestAttribute
    fun failureAndCancellationRunProducerFinallyExactlyOnce() {
        for (error in listOf<Throwable>(IllegalStateException("failure"), CancellationException("cancel"))) {
            val guard = ActionGuard()
            val gate = ActionGate()
            val completion = start { guard.withGuard { gate.await(); "not reached" } }
            check(completion.outcome == null && guard.locked && guard.exits == 0)
            gate.fail(error)
            check(completion.outcome!!.exceptionOrNull() === error)
            checkReleased(guard)
        }
    }

    @TestAttribute
    fun nonLocalReturnRunsProducerAndConsumerFinally() {
        val guard = ActionGuard()
        val gate = ActionGate()
        var cleaned = false
        val completion = start {
            try { returnFromAction(guard, gate) } finally { cleaned = true }
        }
        check(completion.outcome == null && guard.locked && !cleaned)
        gate.release()
        check(completion.outcome!!.getOrThrow() == "non-local" && cleaned)
        checkReleased(guard)
    }

    @TestAttribute
    fun genericAndMemberInlineActionsKeepTheirSourceResult() {
        val guard = ActionGuard()
        val gate = ActionGate()
        val completion = start { genericAction(guard, gate, 73) }
        check(completion.outcome == null && guard.locked)
        gate.release()
        check(completion.outcome!!.getOrThrow() == 73)
        checkReleased(guard)

        val memberGate = ActionGate()
        val memberEntry = ActionGate()
        val member = MemberActions(memberEntry)
        val memberCompletion = start { member.guardedAction { memberGate.await(); "member" } }
        check(memberCompletion.outcome == null && !member.guard.locked)
        memberEntry.release()
        check(memberCompletion.outcome == null && member.guard.locked && member.guard.heldOwner === member)
        memberGate.release()
        check(memberCompletion.outcome!!.getOrThrow() == "member")
        checkReleased(member.guard)
        val immediate = MemberActions()
        check(start { immediate.guardedAction { 91 } }.outcome!!.getOrThrow() == 91)
        checkReleased(immediate.guard)
    }
}
