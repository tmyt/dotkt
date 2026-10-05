// Delegate overload resolution against BCL APIs. This belongs to the interop lane because the subject is choosing
// between .NET delegate-typed overloads, not Kotlin lambda construction itself.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Thread
import System.Threading.ThreadStart
import System.Threading.Tasks.Task
import System.Action

class DelegateOverloadTests {
    @TestAttribute
    fun explicitUnitSamSelectsDelegateOverload() {
        val log = mutableListOf<String>()
        val thread = Thread(ThreadStart { log.add("x"); Unit })
        thread.Start()
        thread.Join()
        val task = Task.Run(Action { log.add("y"); Unit })
        task.Wait()
        assertEquals("x|y", log.joinToString("|"))
    }
}
