package referencedrichenumspill

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*
import kotlin.time.DurationUnit

private class Gate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
}

private class Completion<T> : Continuation<T> {
    var outcome: Result<T>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) { outcome = result }
}

private fun <T> start(block: suspend () -> T): Completion<T> {
    val completion = Completion<T>()
    block.startCoroutine(completion)
    return completion
}

private class Source(var unit: DurationUnit) {
    var reads: Int = 0
    fun read(): DurationUnit { reads++; return unit }
}

private fun choose(unit: DurationUnit, ignored: Int): DurationUnit = unit

class ReferencedRichEnumSpillTests {
    @TestAttribute fun ordinalIsSpilledBeforeTheRightOperandSuspends() {
        val source = Source(DurationUnit.MINUTES)
        val gate = Gate<Int>()
        val completion = start { source.read().ordinal + gate.await() }
        check(completion.outcome == null && source.reads == 1)
        source.unit = DurationUnit.DAYS
        gate.resume(7)
        check(completion.outcome!!.getOrThrow() == 11)
        check(source.reads == 1)
    }

    @TestAttribute fun nameIsSpilledBeforeTheRightOperandSuspends() {
        val source = Source(DurationUnit.HOURS)
        val gate = Gate<String>()
        val completion = start { source.read().name + gate.await() }
        check(completion.outcome == null && source.reads == 1)
        source.unit = DurationUnit.DAYS
        gate.resume("!")
        check(completion.outcome!!.getOrThrow() == "HOURS!")
        check(source.reads == 1)
    }

    @TestAttribute fun entryIdentitySurvivesASuspendingArgument() {
        val gate = Gate<Int>()
        val completion = start { choose(DurationUnit.SECONDS, gate.await()) }
        check(completion.outcome == null)
        gate.resume(7)
        check(completion.outcome!!.getOrThrow() === DurationUnit.SECONDS)
    }
}
