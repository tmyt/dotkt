package roundtriptests.multimodulesuspend

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.inheritedsuspend.Gate
import roundtrip.multimodulesuspend.awaitFromSecondModule

private class Completion : Continuation<String> {
    var outcome: Result<String>? = null
    override val context: CoroutineContext get() = EmptyCoroutineContext
    override fun resumeWith(result: Result<String>) { outcome = result }
}

private suspend fun awaitBoth(first: Gate<String>, second: Gate<String>): String =
    first.await() + awaitFromSecondModule(second)

class MultiModuleSuspendTests {
    @TestAttribute
    fun consumerWithTwoSuspendLibrariesResumesAcrossBothModules() {
        val first = Gate<String>()
        val second = Gate<String>()
        val completion = Completion()
        val action: suspend () -> String = { awaitBoth(first, second) }
        action.startCoroutine(completion)
        assertTrue(completion.outcome == null)
        first.resume("first:")
        assertTrue(completion.outcome == null)
        second.resume("second")
        assertEquals("first:second", completion.outcome!!.getOrThrow())
    }
}
