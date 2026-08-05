import ExtensionAwaitable.Operation
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn
import ExtensionAwaitable.OperationExtensions.await

private suspend fun awaitExtensionOperation(value: Int, synchronous: Boolean): Int =
    Operation<Int>(value, synchronous).await() + 1

class ExtensionAwaitTests {
    @TestAttribute
    fun genericExtensionGetAwaiterSupportsFastAndSuspendingPaths() {
        assertEquals(8, blockOn { awaitExtensionOperation(7, true) })
        assertEquals(42, blockOn { awaitExtensionOperation(41, false) })
    }
}
