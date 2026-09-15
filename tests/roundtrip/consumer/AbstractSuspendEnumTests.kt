package roundtriptests.abstractsuspendenum

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.abstractsuspendenum.*

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
    fun importedInheritedAbstractInterfaceSlotDispatches() {
        val first: EnumReader = InheritedSuspendReader.FIRST
        val second: InheritedSuspendReader = InheritedSuspendReader.SECOND
        assertEquals("first", start { first.read() }.outcome!!.getOrThrow())
        assertEquals("second", start { second.read() }.outcome!!.getOrThrow())
    }

    @TestAttribute
    fun importedBoundedGenericSlotPreservesItsFrame() {
        val entry: BoundedSuspendEcho = BoundedSuspendEcho.VALUE
        val text = EnumGate<String>()
        val completion = start { entry.read(text) }
        assertTrue(completion.outcome == null)
        text.resume("bounded")
        assertEquals("bounded", completion.outcome!!.getOrThrow())
        val numbers = EnumGate<Int>()
        val number = start { entry.read(numbers) }
        assertTrue(number.outcome == null)
        numbers.resume(19)
        assertEquals(19, number.outcome!!.getOrThrow())
    }

    @TestAttribute
    fun importedAbstractEnumReturnsUnitThroughBaseSlot() {
        val entry: SuspendAction = SuspendAction.VALUE
        assertTrue(start<Any?> { entry.run() }.outcome!!.getOrThrow() === Unit)
        assertTrue(start<Any?> { SuspendAction.VALUE.run() }.outcome!!.getOrThrow() === Unit)
    }

    @TestAttribute
    fun importedGenericAbstractSlotSuspendsAndResumes() {
        val entry: SuspendEcho = SuspendEcho.VALUE
        val gate = EnumGate<String>()
        val completion = start { entry.read(gate) }
        assertTrue(completion.outcome == null)
        gate.resume("cross-DLL")
        assertEquals("cross-DLL", completion.outcome!!.getOrThrow())
        val numbers = EnumGate<Int>()
        val number = start { entry.read(numbers) }
        assertTrue(number.outcome == null)
        numbers.resume(73)
        assertEquals(73, number.outcome!!.getOrThrow())
    }
}
