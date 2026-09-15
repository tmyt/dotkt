package roundtriptests.nullablesuspendstruct

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ArraySegment
import kotlin.coroutines.*
import roundtrip.nullablesuspendstruct.*

private class ConsumerSegmentSource : SegmentSource {
    override suspend fun read(present: Boolean): ArraySegment<String?>? = nullableSegment(present)
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

class NullableSuspendStructTests {
    @TestAttribute
    fun consumerImplementationKeepsImportedNullableStructSlot() {
        val source: SegmentSource = ConsumerSegmentSource()
        assertTrue(start { source.read(false) }.outcome!!.getOrThrow() == null)
        assertEquals(2, start { source.read(true) }.outcome!!.getOrThrow()!!.Count)
    }

    @TestAttribute
    fun importedNullableStructPreservesNullAndPresent() {
        assertTrue(start { nullableSegment(false) }.outcome!!.getOrThrow() == null)
        assertEquals(2, start { nullableSegment(true) }.outcome!!.getOrThrow()!!.Count)
        val source: SegmentSource = SegmentSourceImpl()
        assertTrue(start { source.read(false) }.outcome!!.getOrThrow() == null)
        assertEquals(2, start { source.read(true) }.outcome!!.getOrThrow()!!.Count)
    }

    @TestAttribute
    fun importedNullableStructResumesThroughSuspendLambda() {
        for (present in listOf(false, true)) {
            val gate = SegmentGate()
            val action: suspend () -> ArraySegment<String?>? = { delayedSegment(gate) }
            val completion = start(action)
            assertTrue(completion.outcome == null)
            gate.complete(present)
            val result = completion.outcome!!.getOrThrow()
            assertEquals(present, result != null)
            if (present) assertEquals(2, result!!.Count)
        }
    }
}
