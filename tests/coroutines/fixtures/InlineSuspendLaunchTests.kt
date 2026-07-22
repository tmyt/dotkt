// CorB batch — il-inlsuspendlaunch: the `withLock { launch { …local… } }`-shaped cell (#75 BATCH B, L518). A
// coroutine-builder SUSPEND lambda built INSIDE an inline-call lambda arg, capturing that arg's OWN local. All
// top-level decls carry the `ilau` case token under the shared `corB`/`CorB` prefix so their simple names are UNIQUE
// across this assembly (bir2cir's cold-core suspend lowering keys top-level suspend funs by simple name). The former
// `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun corBIlauAddA(a: Int, b: Int): Int = a + b

fun corBIlauLaunchLike(block: suspend () -> Int): Int = blockOn(block)

inline fun corBIlauWithGuard(body: () -> Int): Int = body()

class InlineSuspendLaunchTests {
    @TestAttribute
    fun suspendLambdaCapturingInlineArgLocal() {
        val r = corBIlauWithGuard {
            val local = 30
            corBIlauLaunchLike { corBIlauAddA(local, 12) }
        }
        assertEquals(42, r)   // 42
        val r2 = corBIlauWithGuard {
            val base = 5
            corBIlauLaunchLike { corBIlauAddA(base, base) }
        }
        assertEquals(10, r2)  // 10
    }
}
