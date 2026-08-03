// #220 — a SUSPEND function type has no delegate arity limit, because it is not a delegate.
//
// A non-suspend function type of 23 parameters is refused: System.Func/Action carry 0..16, the stdlib's canonical
// KFunc/KAction carry 17..22, and the family cannot grow another row. A `suspend` function type never reaches that
// family at all — it lowers to `object` plus a [KotlinSuspendFunctionType] carrier (dotkt-semantics 4, 8e-bis), so
// its arity costs nothing and no limit applies. Measured, not assumed: across the whole stdlib build every suspend
// function type reaching the delegate path has arity 1 (the `sequence {}` receiver lambda, whose arity the stdlib
// signature fixes), and an app's own suspend lambda is replaced by its state machine before type lowering runs.
//
// This fixture is the shape that would otherwise be assumed broken by proximity to the refusal: 23 parameters, in
// the positions a suspend function type is used in — parameter, return, local, and extension receiver (which counts
// toward the arity for a delegate, and equally does not matter here).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun wideSuspendParam(f: suspend (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = f(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)

suspend fun wideSuspendReturn(): suspend (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22, p23 -> p1 + p23 }

suspend fun wideSuspendReceiver(f: suspend Int.(Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int): Int = 1.f(2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)

class WideSuspendFunctionTypeTests {
    // Parameter and return position, plus a local slot: none of these becomes a delegate, so 23 is as ordinary as 2.
    @TestAttribute
    fun aSuspendFunctionTypeHasNoDelegateArityLimit() {
        assertEquals(24, blockOn { wideSuspendParam({ p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22, p23 -> p1 + p23 }) })
        assertEquals(24, blockOn { wideSuspendReturn()(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23) })
        assertEquals(23, blockOn {
            val local: suspend (Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Int = { p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12, p13, p14, p15, p16, p17, p18, p19, p20, p21, p22, p23 -> p1 * p23 }
            local(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23)
        })
    }

    // An extension receiver occupies a delegate slot and so counts toward the arity of a NON-suspend function type.
    // Here it counts toward nothing, which is the point: 22 parameters plus a receiver is not 23-of-anything.
    @TestAttribute
    fun aSuspendReceiverFunctionTypeIsUnaffectedToo() {
        // The receiver is 1 and the 22 arguments are 2..23, so `this + a22` is 1 + 23.
        assertEquals(24, blockOn { wideSuspendReceiver({ a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13, a14, a15, a16, a17, a18, a19, a20, a21, a22 -> this + a22 }) })
    }
}
