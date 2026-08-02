// feature fixture — il-inlsuspendcarrier: the SUSPEND carrier-VALUE contract for the inline splicer (#75 BATCH B). An
// `inline fun` with a crossinline SUSPEND lambda whose body builds a CAPTURING suspend lambda passed to a NON-inline
// fn (`dotkt.support.blockOn`). All top-level decls carry the `iscar` case token under the shared `escapingSuspend`/`EscapingSuspend`
// prefix so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level
// suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun escapingSuspendIscarAddA(a: Int, b: Int): Int = a + b

inline fun escapingSuspendIscarWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

inline fun escapingSuspendIscarWrapPlus(x: Int, bonus: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) + bonus }

class EscapingSuspendLambdaTests {
    @TestAttribute
    fun escapingCapturingSuspendLambda() {
        assertEquals(42, escapingSuspendIscarWrap(20) { escapingSuspendIscarAddA(it, 22) })          // 42
        assertEquals(42, escapingSuspendIscarWrapPlus(10, 2, { escapingSuspendIscarAddA(it, 30) }))  // 10+30=40, +2 = 42
        assertEquals(7, escapingSuspendIscarWrap(0) { escapingSuspendIscarAddA(it, 7) })             // 7
    }
}
