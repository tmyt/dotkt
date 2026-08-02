// feature fixture — il-inlsuspendlaunch: the `withLock { launch { …local… } }`-shaped cell (#75 BATCH B, L518). A
// coroutine-builder SUSPEND lambda built INSIDE an inline-call lambda arg, capturing that arg's OWN local. All
// top-level decls carry the `ilau` case token under the shared `inlineSuspendLaunch`/`InlineSuspendLaunch` prefix so their simple names are UNIQUE
// across this assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). The former
// `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun inlineSuspendLaunchIlauAddA(a: Int, b: Int): Int = a + b

fun inlineSuspendLaunchIlauLaunchLike(block: suspend () -> Int): Int = blockOn(block)

inline fun inlineSuspendLaunchIlauWithGuard(body: () -> Int): Int = body()

class InlineSuspendLaunchTests {
    @TestAttribute
    fun suspendLambdaCapturingInlineArgLocal() {
        val r = inlineSuspendLaunchIlauWithGuard {
            val local = 30
            inlineSuspendLaunchIlauLaunchLike { inlineSuspendLaunchIlauAddA(local, 12) }
        }
        assertEquals(42, r)   // 42
        val r2 = inlineSuspendLaunchIlauWithGuard {
            val base = 5
            inlineSuspendLaunchIlauLaunchLike { inlineSuspendLaunchIlauAddA(base, base) }
        }
        assertEquals(10, r2)  // 10
    }
}
