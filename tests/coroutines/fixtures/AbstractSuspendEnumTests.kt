package abstractsuspendenum

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*

private enum class Action {
    FIRST { override suspend fun run() {} },
    SECOND { override suspend fun run() {} };
    abstract suspend fun run()
}

private interface Reader { suspend fun read(): String }
private enum class InheritedReader : Reader {
    FIRST { override suspend fun read(): String = "first" },
    SECOND { override suspend fun read(): String = "second" }
}

private class Gate<T> {
    private var pending: Continuation<T>? = null
    suspend fun await(): T = suspendCoroutine { pending = it }
    fun resume(value: T) { pending!!.resume(value) }
}

private enum class Echo {
    VALUE { override suspend fun <T> read(gate: Gate<T>): T = gate.await() };
    abstract suspend fun <T> read(gate: Gate<T>): T
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

class AbstractSuspendEnumTests {
    @TestAttribute
    fun entryCallReturnsUnitAsValue() {
        val completion = start<Any?> { Action.FIRST.run() }
        assertTrue(completion.outcome!!.getOrThrow() === Unit)
    }

    @TestAttribute
    fun baseTypedCallsDispatchToEachEntry() {
        for (entry in Action.values()) {
            val completion = start<Any?> { entry.run() }
            assertTrue(completion.outcome!!.getOrThrow() === Unit)
        }
    }

    @TestAttribute
    fun inheritedAbstractInterfaceSlotDispatches() {
        val first: Reader = InheritedReader.FIRST
        val second: InheritedReader = InheritedReader.SECOND
        assertEquals("first", start { first.read() }.outcome!!.getOrThrow())
        assertEquals("second", start { second.read() }.outcome!!.getOrThrow())
    }

    @TestAttribute
    fun genericAbstractMemberResumesWithItsResult() {
        val entry: Echo = Echo.VALUE
        val text = Gate<String>()
        val completion = start { entry.read(text) }
        assertTrue(completion.outcome == null)
        text.resume("resumed")
        assertEquals("resumed", completion.outcome!!.getOrThrow())
        val number = Gate<Int>()
        val integer = start { entry.read(number) }
        assertTrue(integer.outcome == null)
        number.resume(42)
        assertEquals(42, integer.outcome!!.getOrThrow())
    }
}
