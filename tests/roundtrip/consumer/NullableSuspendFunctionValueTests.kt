package roundtriptests.nullablesuspendvalues

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import roundtrip.nullablesuspendvalues.*

private class Completion<T> : Continuation<T> {
    override val context: CoroutineContext = EmptyCoroutineContext
    var completed = false
    var result: Result<T>? = null
    override fun resumeWith(result: Result<T>) { this.result = result; completed = true }
}

private class LocalNamedInvoker {
    suspend operator fun invoke(value: String): String = value
}

class NullableSuspendFunctionValueTests {
    @TestAttribute
    fun sameModuleAndImportedFunctionValues() {
        val completion = Completion<Unit>()
        val work: suspend () -> Unit = {
            checkNullableSuspendValues()
            check(nullableSuspendIdentity(true)!!(null) == null)
            check(nullableSuspendIdentity(true)!!("imported") == "imported")
            check(nullableSuspendZero()!!() == 17)
            check(nullableSuspendTwo()!!(19, 23) == 42)
            val local = nullableSuspendIdentity(true)
            if (local != null) check(local("local") == "local")
            check(NullableSuspendHolder(local).callback!!("field") == "field")
            check(nullableSuspendIdentity(false)?.invoke("absent") == null)
            check(nullableSuspendIdentity(true)?.invoke("present") == "present")
        }
        work.startCoroutine(completion)
        check(completion.completed)
        completion.result!!.getOrThrow()
    }

    @TestAttribute
    fun importedNullableValueActuallySuspendsAndResumes() {
        val gate = NullableSuspendGate()
        val completion = Completion<String?>()
        var calls = 0
        fun selected(): (suspend (String?) -> String?)? { calls++; return gate.callback() }
        val work: suspend () -> String? = { selected()!!("resumed") }
        work.startCoroutine(completion)
        check(calls == 1 && gate.entries == 1 && !completion.completed)
        gate.release()
        check(completion.completed && completion.result!!.getOrThrow() == "resumed")
        check(calls == 1 && gate.entries == 1)

        val safeCompletion = Completion<String?>()
        val safeWork: suspend () -> String? = { gate.callback()?.invoke("safe") }
        safeWork.startCoroutine(safeCompletion)
        check(!safeCompletion.completed && gate.entries == 2)
        gate.release()
        check(safeCompletion.completed && safeCompletion.result!!.getOrThrow() == "safe")
    }

    @TestAttribute
    fun suspendedReceiverProductionPrecedesValueInvocation() {
        val gate = NullableSuspendGate()
        val completion = Completion<String?>()
        val work: suspend () -> String? = { gate.select()!!("selected") }
        work.startCoroutine(completion)
        check(!completion.completed && gate.entries == 1)
        gate.release()
        check(!completion.completed && gate.entries == 2)
        gate.release()
        check(completion.completed && completion.result!!.getOrThrow() == "selected")
        check(gate.entries == 2)
    }

    @TestAttribute
    fun nullableNominalReceiverKeepsNamedSuspendInvoke() {
        val gate = NullableSuspendGate()
        val runner: NamedSuspendInvoker? = NamedSuspendInvoker(gate)
        val completion = Completion<String>()
        val work: suspend () -> String = {
            val local: LocalNamedInvoker? = LocalNamedInvoker()
            check(local!!("local") == "local")
            runner!!.invoke("named")
        }
        work.startCoroutine(completion)
        check(!completion.completed && gate.entries == 1)
        gate.release()
        check(completion.completed && completion.result!!.getOrThrow() == "named")
    }
}
