// feature fixture — il-inlsuspendcarrier: the SUSPEND carrier-VALUE contract for the inline splicer (#75 BATCH B). An
// `inline fun` with a crossinline SUSPEND lambda whose body builds a CAPTURING suspend lambda passed to a NON-inline
// fn (`dotkt.support.blockOn`). All top-level declarations use the descriptive `escapingSuspendCarrier`/
// `EscapingSuspendCarrier` stem so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level
// suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun escapingSuspendCarrierAdd(a: Int, b: Int): Int = a + b

inline fun escapingSuspendCarrierWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

inline fun escapingSuspendCarrierWrapPlus(x: Int, bonus: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) + bonus }

class EscapingSuspendLambdaTests {
    @TestAttribute
    fun escapingCapturingSuspendLambda() {
        assertEquals(42, escapingSuspendCarrierWrap(20) { escapingSuspendCarrierAdd(it, 22) })          // 42
        assertEquals(42, escapingSuspendCarrierWrapPlus(10, 2, { escapingSuspendCarrierAdd(it, 30) }))  // 10+30=40, +2 = 42
        assertEquals(7, escapingSuspendCarrierWrap(0) { escapingSuspendCarrierAdd(it, 7) })             // 7
    }
}
