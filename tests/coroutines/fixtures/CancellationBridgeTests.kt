// suspend->Task<R> bridge cancellation battery. Exercise the public CLR Task bridge itself: bir2cir synthesizes
// its module-private root Continuation and maps cancellation failures to a CANCELED Task, while ordinary failures
// remain FAULTED. Reflection calls the physical Task-returning entry because Kotlin source correctly sees these
// declarations as suspend functions and would otherwise route a call to their cold entries.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.OperationCanceledException
import System.Type
import System.Threading.Tasks.Task1
import kotlin.coroutines.cancellation.CancellationException

suspend fun cancellationBridgeOce(): Int =
    throw OperationCanceledException()

suspend fun cancellationBridgeKotlinCancellation(): Int =
    throw CancellationException("stop")

suspend fun cancellationBridgeFailure(): Int =
    throw IllegalStateException("boom")

suspend fun cancellationBridgeSuccess(): Int = 42

private fun invokeCancellationBridge(name: String): Task1<Int> =
    Type.GetType("CancellationBridgeTestsKt")!!
        .GetMethod(name)!!
        .Invoke(null, null) as Task1<Int>

class CancellationBridgeTests {
    @TestAttribute
    fun operationCanceledExceptionCompletesCanceledTask() {
        val task = invokeCancellationBridge("cancellationBridgeOce")
        assertEquals(true, task.IsCanceled)
        assertEquals(false, task.IsFaulted)
    }

    @TestAttribute
    fun kotlinCancellationCompletesCanceledTask() {
        val task = invokeCancellationBridge("cancellationBridgeKotlinCancellation")
        assertEquals(true, task.IsCanceled)
        assertEquals(false, task.IsFaulted)
    }

    @TestAttribute
    fun ordinaryFailureAndSuccessKeepTheirTaskStates() {
        val failed = invokeCancellationBridge("cancellationBridgeFailure")
        assertEquals(false, failed.IsCanceled)
        assertEquals(true, failed.IsFaulted)

        val succeeded = invokeCancellationBridge("cancellationBridgeSuccess")
        assertEquals(false, succeeded.IsCanceled)
        assertEquals(false, succeeded.IsFaulted)
        assertEquals(42, succeeded.Result)
    }
}
