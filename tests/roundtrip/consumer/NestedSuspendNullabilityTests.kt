package roundtriptests.nestedsuspendnullability

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.coroutines.*
import roundtrip.nestedsuspendnullability.*

class NestedSuspendNullabilityTests {
    @TestAttribute
    fun importedSuspendResultsPreserveLogicalNullability() {
        var done = false
        val block: suspend () -> Unit = {
            val nullable: InvariantBox<String?>? = nullableBox()
            val nonNull: InvariantBox<String?> = nonNullBox()
            val array: Array<String?>? = nullableArray()
            val nested: InvariantBox<Array<String?>?> = nestedArray()
            val unit: InvariantBox<InvariantBox<Unit?>?> = nestedUnit()
            assertTrue(nullable!!.value == null)
            assertTrue(nonNull.value == null)
            assertTrue(array!![0] == null)
            assertTrue(nested.value!![0] == null)
            assertTrue(unit.value!!.value == null)
        }
        block.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext get() = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) {
                result.getOrThrow()
                done = true
            }
        })
        assertTrue(done)
    }
}
