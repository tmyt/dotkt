package capturedlocalsuspendextension

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

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
            current
        }
        assertEquals(25, result)
    }
}
