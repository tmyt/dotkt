// C#-producer roundtrip consumer battery B — .NET out/ref byref interop (#52).
//   outref <- il-outref  `byref(v)` marks a .NET out/ref param (surfaced ClrRef<T>): the backend passes the
//                        variable's address and selects the out/ref overload; `out`/`ref` unify to one `byref`.
//                        A ref-returning method received plainly is a value copy; received via `var x by byref(m())`
//                        it is a LIVE ref (getValue/setValue inline to ldobj/stobj) so writes flow back.
// `@ClrField` is recognized by SHORT NAME (bir2cir), so declaring the annotation here keeps the sample standalone.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import OutRef.Calc
import kotlin.clr.byref
import kotlin.clr.ClrRef
import kotlin.clr.stackBuffer

annotation class ClrField

class OutRefAcc {
    var quo: Int = -1           // default -> CLR property (internal backing field; byref-able in-module)
    @ClrField var raw: Int = 7  // opt-in plain public CLR field
}

// AN ADDRESS SLOT'S EVALUATION ORDER, at a call that also fills a default a later default reads. The fill is shared,
// so it becomes a local ahead of the call; the address is not a value and cannot join it, so what has to be pinned at
// the address's own position is the value its LOCATION is computed from (`byRefOrderMk()`), leaving `<local>.n` in the
// slot. Kotlin's order is `m` then `d`; pinning after the fill's local would emit `d` then `m`.
//
// COMPILED, DELIBERATELY NOT RUN. A Kotlin function declaring a `ClrRef<T>` PARAMETER is separately broken — it
// NullReferenceExceptions on entry with no default arguments and no plan in sight, on `origin/main` too — so calling
// these would assert that unrelated defect rather than this order. What they do buy: kotc emits the plan, bir2cir
// lowers it, ilemit emits it and ILVerify checks the result. Turn each into a `@TestAttribute` asserting the log
// noted beside it once the `ClrRef`-parameter defect is fixed; they guard both from then on.
private class ByRefOrderHolder(var n: Int)
private var byRefOrderLog = ""
private fun byRefOrderMk(): ByRefOrderHolder { byRefOrderLog += "m"; return ByRefOrderHolder(1) }
private fun byRefOrderD(): Int { byRefOrderLog += "d"; return 3 }
private fun byRefOrderP(): Int { byRefOrderLog += "p"; return 2 }
private fun byRefOrderIdx(): Int { byRefOrderLog += "i"; return 0 }
private fun byRefOrderTake(r: ClrRef<Int>, a: Int = byRefOrderD(), b: Int = a * 10): Int = a + b
private fun byRefOrderTakeP(p: Int, r: ClrRef<Int>, a: Int = byRefOrderD(), b: Int = a * 10): Int = p + a + b
// "md" — the address's operand is pinned, then the shared fill.
private fun byRefOrderCall(): Int = byRefOrderTake(byref(byRefOrderMk().n))
// "pmd" — a supplied value sits BEFORE the address in Kotlin's order, and the shared fill's local sits after both.
// This is the one that discriminates: collecting the address pins into their own list and emitting that list ahead of
// the materialised locals gives "mpd".
private fun byRefOrderCallP(): Int = byRefOrderTakeP(byRefOrderP(), byref(byRefOrderMk().n))
// "id" — the impure operand is an INDEX, not a receiver. A walk that only knew about `recv` pinned nothing here and
// left `idx()` to run at the slot, i.e. after the fill's local.
private fun byRefOrderCallIndexed(arr: IntArray): Int = byRefOrderTake(byref(arr[byRefOrderIdx()]))
// "pmd" — the fill is NOT shared by another default, so it needs no local of its own; the only reason anything moves
// is ORDER. Its slot sits ahead of both supplied ones while Kotlin evaluates it last, and the address contributes a
// pre-call pin. That makes the ordering rule one about pre-call WORK rather than about materialisation: a binding that
// emits any pre-call statement forces every earlier non-stable binding to emit one too, or the earlier value is left
// inline in the call and runs after it.
private fun byRefOrderTakeUnshared(a: Int = byRefOrderD(), p: Int, r: ClrRef<Int>): Int = p + a
private fun byRefOrderCallUnshared(): Int =
    byRefOrderTakeUnshared(p = byRefOrderP(), r = byref(byRefOrderMk().n))
// The pinned operand is an `arrayGet`, which carries neither `sty` nor `type` at the kotc boundary — it is typed from
// its `elem`. An untyped pin is not a lesser local, it is unverifiable IL.
private class ByRefOrderCell(var n: Int)
private fun byRefOrderCells(): Array<ByRefOrderCell> { byRefOrderLog += "c"; return arrayOf(ByRefOrderCell(1)) }
private fun byRefOrderCallElem(): Int = byRefOrderTake(byref(byRefOrderCells()[0].n))
// A location whose ROOT is a CALL is not an lvalue former: pinning its operands would still leave the invocation
// running at the slot, once but after the fill's local. The whole location moves to its own plan position instead, and
// WHICH local it moves into is decided by the location's own DECLARED type, never by its node shape — storing a `T`
// into a `T&` slot is unverifiable IL, and the frontend accepts `byref(<rvalue call>)`.
//
// "md" — an ORDINARY call: a plain `Int` local at the plan position, and the slot takes THAT local's address, which is
// what taking the address of an rvalue means.
private fun byRefOrderPlain(): Int { byRefOrderLog += "m"; return 1 }
private fun byRefOrderCallRvalue(): Int = byRefOrderTake(byref(byRefOrderPlain()))
// "id" — a .NET ref-RETURNING member takes the SAME path, because the Kotlin surface erases its ref-ness: `Slot`
// reads back as `fun Slot(i: Int): Int`, which is also why `val v = c.Slot(1)` is documented as a value copy. So this
// argument is the address of a copy. The live-ref form is the delegate one (`var x by byref(c.Slot(1))`), which is
// typed `ClrRef<Int>` and keeps the pointer — see `byrefDelegateForwardedToARefParameter`.
private fun byRefOrderCallRefReturn(c: Calc): Int = byRefOrderTake(byref(c.Slot(byRefOrderIdx())))

// A singleton's properties, so `byref(BrHold.a)` names a STATIC storage location rather than an instance field.
object BrHold { var a = 1; var b = 2 }
// The location's impure operand is an INLINE-SPLICED block, which carries no type stamp of its own — its type is its
// result's. An untyped pin would abort the build on source the frontend accepted.
private fun byRefOrderCallSplicedOperand(): Int = byRefOrderTake(byref(run { byRefOrderMk() }.n))

class ByRefParameterTests {
    // A `var x by byref(...)` delegate read passed ON to a `ref` parameter. The read is a `byrefLoad` — the pointee —
    // and its ADDRESS is the pointer the delegate holds, not the address of a copy of it. Taking the copy's address is
    // verifiable IL that swaps two temporaries and drops both writes, which is the worst direction for a defect to
    // fail in: green ILVerify, green types, silently wrong program. No `ClrRef` PARAMETER is involved, so this one
    // runs.
    @TestAttribute
    fun byrefDelegateForwardedToARefParameter() {
        val c = Calc()
        var a by byref(c.Slot(0))                           // live refs into the producer's own array
        var b by byref(c.Slot(1))
        assertEquals("10 20", "$a $b")                      // the array starts 10, 20, 30
        c.Swap(byref(a), byref(b))                          // ref params -> must swap THROUGH the delegates
        assertEquals("20 10", "$a $b")                      // 20 10  (was "10 20": the swap hit two temporaries)
        assertEquals("20 10", "${c.Slot(0)} ${c.Slot(1)}")  // ...and it landed in the producer's array
    }

    // The other GENUINE lvalues a by-reference argument can name. `EmitAddr` had arms for a local, `this` and a
    // direct field only; everything else fell to the rvalue path — materialize into a temporary and hand out ITS
    // address — which is verifiable IL that swaps two temporaries and drops both writes. Same silent-lost-write class
    // as the delegate read above, so these assert that the write LANDS in the storage the source named.
    @TestAttribute
    fun byrefOfAnArrayElementAndAStaticField() {
        val c = Calc()

        val xs = intArrayOf(10, 20)
        c.Swap(byref(xs[0]), byref(xs[1]))                  // ldelema, not a copy
        assertEquals("20 10", "${xs[0]} ${xs[1]}")          // 20 10  (was "10 20")

        BrHold.a = 1
        BrHold.b = 2
        c.Swap(byref(BrHold.a), byref(BrHold.b))            // a static field's own address
        assertEquals("2 1", "${BrHold.a} ${BrHold.b}")      // 2 1    (was "1 2")
    }

    // A STACK-BUFFER slot by reference, with a SIDE-EFFECTING index. The bounds check and the address computation are
    // one access, so the index is evaluated once: emitting them as two pieces incremented `i` twice per argument, and
    // the second bounds check then ran against a different element than the access.
    @TestAttribute
    fun byrefOfAStackSlotEvaluatesItsIndexOnce() {
        val c = Calc()
        val log = stackBuffer<Int, String>(4) { b ->
            b[0] = 10; b[1] = 20
            var i = 0
            c.Swap(byref(b[i++]), byref(b[i++]))
            "$i ${b[0]} ${b[1]}"
        }
        assertEquals("2 20 10", log)                        // 2 increments (not 4), and the slots swapped
    }

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
