package roundtrip.suspendphysicalbounds

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

private class Completion : Continuation<Unit> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var result: Result<Unit>? = null
    override fun resumeWith(result: Result<Unit>) { this.result = result }
}

class SuspendDefaultPhysicalBoundTests {
    @TestAttribute
    fun importedDefaultKeepsItsDonorsPhysicalOwnerBound() {
        var effects = 0
        val block: suspend () -> Unit = {
            check(PhysicalBoundOwner("label", 17).matches<String>({ effects++ }, "text") == Pair(17, true))
            check(PhysicalBoundOwner("label", "owner").matches<Int>({ effects++ }, "text") == Pair("owner", false))
            check(PhysicalBoundOwner("label", 23).matches<String?>({ effects++ }, null) == Pair(23, true))
            check(PhysicalBoundOwner("label", "nullable").matches<Int>({ effects++ }, null) == Pair("nullable", false))
        }
        val completion = Completion()
        block.startCoroutine(completion)
        check(completion.result != null)
        completion.result!!.getOrThrow()
        check(effects == 4)
    }
}
