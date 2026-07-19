// Coroutine battery — migrates cases/il-lam2 and il-cobuild. Both drive the cold-core coroutine machine
// through `dotkt.support.blockOn` (harness/Coroutines.kt — ONE shared harness, not 36 copies). blockOn is a
// blocking call returning the coroutine's result, so it asserts as an ordinary value. In the full harness the
// audit's determinism concern (§10: Task.Delay(1) may fast-path) is addressed separately with a TCS/barrier
// battery; il-cobuild is migrated here verbatim to preserve its exact end-to-end suspend-resume check.
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert

// il-lam2: a capturing suspend lambda ({ h() + n } captures n) with a real suspend call.
suspend fun h(): Int = 5

// il-cobuild: real Task.Delay(1).await() suspension + direct Kotlin->Kotlin suspend calls (cold entry).
suspend fun compute(n: Int): Int {
    Task.Delay(1).await()
    return n * n
}
suspend fun total(): Int {
    val a = compute(3)
    val b = compute(4)
    return a + b   // 9 + 16
}

class CoroutinesTests {
    @TestAttribute
    fun capturingSuspendLambda() {
        val n = 10
        ClassicAssert.AreEqual(15, blockOn { h() + n })
    }

    @TestAttribute
    fun coldEntrySuspendChain() {
        ClassicAssert.AreEqual(25, blockOn { total() })
    }
}
