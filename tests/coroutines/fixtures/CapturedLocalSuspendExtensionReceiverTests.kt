package capturedlocalsuspendextension

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

private suspend fun <T> constructedSuspendReceiverReference(value: T): Int {
    var current = 0
    suspend fun <U> U.bump() { Task.Delay(1).await(); current++ }
    val reference: suspend () -> Unit = listOf(value)::bump
    reference()
    return current
}

private class GenericSuspendReceiverOwner<A, B>(val first: A, val second: B) {
    suspend fun <T> replace(initial: T, next: T): Pair<A, T> {
        var current = first to initial
        suspend fun String.replacePair() {
            Task.Delay(1).await()
            check(length == 5 && second != null)
            current = first to next
        }
        val reference: suspend () -> Unit = "hello"::replacePair
        reference()
        return current
    }
}

class CapturedLocalSuspendExtensionReceiverTests {
    @TestAttribute
    fun capturedReceiverSurvivesAwaitAndNestedCalls() {
        val result = blockOn {
            var current = 0
            suspend fun String.replaceAfterSuspension() {
                Task.Delay(1).await()
                fun Int.addReceiver() { current = this@replaceAfterSuspension.length + this }
                7.addReceiver()
                Task.Delay(1).await()
                current += length
            }
            "hello".replaceAfterSuspension()
            check(current == 17)
            var receiver = "hello"
            var evaluations = 0
            fun select(): String { evaluations++; return receiver }
            suspend fun String.appendAfterAwait() {
                Task.Delay(1).await()
                current += length
            }
            val bound: suspend () -> Unit = select()::appendAfterAwait
            val unbound: suspend (String) -> Unit = String::appendAfterAwait
            receiver = "abc"
            check(evaluations == 1)
            bound()
            unbound(receiver)
            check(evaluations == 1)
            suspend fun String.noCaptureAwait(): Int { Task.Delay(1).await(); return length }
            val boundWithoutCapture: suspend () -> Int = receiver::noCaptureAwait
            val unboundWithoutCapture: suspend (String) -> Int = String::noCaptureAwait
            check(boundWithoutCapture() == 3)
            check(unboundWithoutCapture("hello") == 5)
            suspend fun <U> U.appendGenericAfterAwait() { Task.Delay(1).await(); current++ }
            val ownGeneric: suspend () -> Unit = 42::appendGenericAfterAwait
            ownGeneric()
            current
        }
        assertEquals(26, result)
        assertEquals(1, blockOn { constructedSuspendReceiverReference("value") })
        assertEquals("owner" to 42, blockOn { GenericSuspendReceiverOwner("owner", true).replace(1, 42) })
    }
}
