// C#-producer roundtrip consumer battery B — .NET out/ref byref interop (#52).
//   outref <- il-outref  `byref(v)` marks a .NET out/ref param (surfaced ClrRef<T>): the backend passes the
//                        variable's address and selects the out/ref overload; `out`/`ref` unify to one `byref`.
//                        A ref-returning method received plainly is a value copy; received via `var x by byref(m())`
//                        it is a LIVE ref (getValue/setValue inline to ldobj/stobj) so writes flow back.
// `@ClrField` is recognized by SHORT NAME (bir2cir), so declaring the annotation here keeps the sample standalone.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import OutRef.Calc
import kotlin.clr.byref

annotation class ClrField

class OutRefAcc {
    var quo: Int = -1           // default -> CLR property (internal backing field; byref-able in-module)
    @ClrField var raw: Int = 7  // opt-in plain public CLR field
}

class InteropBByRefTests {
    @TestAttribute
    fun outref() {
        val c = Calc()
        var q = -1
        val ok = c.TryDivide(10, 2, byref(q))               // out -> writes q=5
        assertEquals("ok=5", if (ok) "ok=$q" else "fail")   // ok=5
        val bad = c.TryDivide(10, 0, byref(q))              // out -> writes q=0, returns false
        assertEquals("fail", if (bad) "ok=$q" else "fail")  // fail

        var x = 1
        var y = 2
        c.Swap(byref(x), byref(y))                          // ref params -> swap in place
        assertEquals("2 1", "$x $y")                        // 2 1

        val v = c.Slot(1)                                   // ref return WITHOUT byref -> value copy
        assertEquals(20, v)                                 // 20

        var slot by byref(c.Slot(1))                        // ref return via `by byref` -> live ref
        assertEquals(20, slot)                              // 20
        slot = 99                                           // write through the ref
        assertEquals(109, c.Slot(0) + c.Slot(1))            // 10 + 99 = 109

        val a = OutRefAcc()
        c.TryDivide(20, 4, byref(a.quo))                    // out -> writes a.quo=5 via ldflda of its backing field
        assertEquals(5, a.quo)                              // 5
        c.Swap(byref(a.quo), byref(a.raw))                  // ref-swap a property-backed field with a @ClrField field
        assertEquals("7 5", "${a.quo} ${a.raw}")            // 7 5
    }
}
