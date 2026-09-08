// feature fixture — il-inlsuspendcarrier: the SUSPEND carrier-VALUE contract for the inline splicer (#75 BATCH B). An
// `inline fun` with a crossinline SUSPEND lambda whose body builds a CAPTURING suspend lambda passed to a NON-inline
// fn (`dotkt.support.blockOn`). All top-level declarations use the descriptive `escapingSuspendCarrier`/
// `EscapingSuspendCarrier` stem so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend lowering keys top-level
// suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun escapingSuspendCarrierAdd(a: Int, b: Int): Int = a + b

inline fun escapingSuspendCarrierWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

inline fun escapingSuspendCarrierWrapPlus(x: Int, bonus: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) + bonus }

class EscapingSuspendCarrierOuterParameterCollision(val base: Int) {
    fun run(): Int = escapingSuspendCarrierWrap(5) { __outer ->
        escapingSuspendCarrierAdd(base, __outer)
    }
}

fun escapingSuspendCarrierMachineryNames(label: Int, completion: Int): Int =
    escapingSuspendCarrierWrap(0) { escapingSuspendCarrierAdd(label, completion) }

class EscapingSuspendCarrierMemberExtension(private val base: Int) {
    private fun String.runCarrier(): Int = escapingSuspendCarrierWrap(5) { value ->
        escapingSuspendCarrierAdd(base + length, value)
    }

    fun run(): Int = "1234567".runCarrier()
}

class EscapingSuspendLambdaTests {
    @TestAttribute
    fun escapingCapturingSuspendLambda() {
        assertEquals(42, escapingSuspendCarrierWrap(20) { escapingSuspendCarrierAdd(it, 22) })          // 42
        assertEquals(42, escapingSuspendCarrierWrapPlus(10, 2, { escapingSuspendCarrierAdd(it, 30) }))  // 10+30=40, +2 = 42
        assertEquals(7, escapingSuspendCarrierWrap(0) { escapingSuspendCarrierAdd(it, 7) })             // 7
        assertEquals(42, EscapingSuspendCarrierOuterParameterCollision(37).run())
        assertEquals(42, escapingSuspendCarrierMachineryNames(20, 22))
        assertEquals(42, EscapingSuspendCarrierMemberExtension(30).run())
    }
}
