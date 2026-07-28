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
import kotlin.clr.ClrRef

annotation class ClrField

class OutRefAcc {
    var quo: Int = -1           // default -> CLR property (internal backing field; byref-able in-module)
    @ClrField var raw: Int = 7  // opt-in plain public CLR field
}

// AN ADDRESS SLOT'S EVALUATION ORDER, at a call that also fills a default a later default reads. The fill is shared,
// so it becomes a local ahead of the call; the address is not a value and cannot join it, so what has to be pinned at
// the address's own position is the value its LOCATION is computed from (`brOrderMk()`), leaving `<local>.n` in the
// slot. Kotlin's order is `m` then `d`; pinning after the fill's local would emit `d` then `m`.
//
// COMPILED, DELIBERATELY NOT RUN. A Kotlin function declaring a `ClrRef<T>` PARAMETER is separately broken — it
// NullReferenceExceptions on entry with no default arguments and no plan in sight, on `origin/main` too — so calling
// these would assert that unrelated defect rather than this order. What they do buy: kotc emits the plan, bir2cir
// lowers it, ilemit emits it and ILVerify checks the result. Turn each into a `@TestAttribute` asserting the log
// noted beside it once the `ClrRef`-parameter defect is fixed; they guard both from then on.
private class BrOrderHolder(var n: Int)
private var brOrderLog = ""
private fun brOrderMk(): BrOrderHolder { brOrderLog += "m"; return BrOrderHolder(1) }
private fun brOrderD(): Int { brOrderLog += "d"; return 3 }
private fun brOrderP(): Int { brOrderLog += "p"; return 2 }
private fun brOrderIdx(): Int { brOrderLog += "i"; return 0 }
private fun brOrderTake(r: ClrRef<Int>, a: Int = brOrderD(), b: Int = a * 10): Int = a + b
private fun brOrderTakeP(p: Int, r: ClrRef<Int>, a: Int = brOrderD(), b: Int = a * 10): Int = p + a + b
// "md" — the address's operand is pinned, then the shared fill.
private fun brOrderCall(): Int = brOrderTake(byref(brOrderMk().n))
// "pmd" — a supplied value sits BEFORE the address in Kotlin's order, and the shared fill's local sits after both.
// This is the one that discriminates: collecting the address pins into their own list and emitting that list ahead of
// the materialised locals gives "mpd".
private fun brOrderCallP(): Int = brOrderTakeP(brOrderP(), byref(brOrderMk().n))
// "id" — the impure operand is an INDEX, not a receiver. A walk that only knew about `recv` pinned nothing here and
// left `idx()` to run at the slot, i.e. after the fill's local.
private fun brOrderCallIndexed(arr: IntArray): Int = brOrderTake(byref(arr[brOrderIdx()]))

class ByRefParameterTests {
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
