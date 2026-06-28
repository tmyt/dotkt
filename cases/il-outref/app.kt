// out/ref interop. `byref(v)` marks an argument as a .NET out/ref parameter (surfaced type ClrRef<T>); the backend
// passes the variable's address and selects the out/ref overload. `out` and `ref` unify to one `byref`.
// A ref-returning method received plainly is a value copy; received via `var x by byref(m())` it is a LIVE ref
// (a `ref T` local, getValue/setValue inline to ldobj/stobj) so writes flow back into the .NET storage. (#52)
import P.Calc
import kotlin.clr.byref   // byref/ClrRef now live in the importable `kotlin.clr` namespace (was the root package)

// CLR property model — Phase 5: `byref(obj.prop)` of an own-source-set property addresses its INTERNAL backing
// field (ldflda), so a .NET out/ref param writes back THROUGH the property. `@ClrField` opts a property out to a
// plain public field; byref of it addresses that field. Both are exercised below. (`ClrField` is recognized by
// short name — the real one is the facadegen-generated `clr.ClrField`; declared here so the sample is standalone.)
annotation class ClrField

class Acc {
    var quo: Int = -1           // default -> CLR property (private-to-consumers, internal backing field; byref-able in-module)
    @ClrField var raw: Int = 7  // opt-in plain public CLR field
}

fun main() {
    val c = Calc()
    var q = -1
    val ok = c.TryDivide(10, 2, byref(q))        // out param -> writes q=5
    println(if (ok) "ok=$q" else "fail")         // ok=5
    val bad = c.TryDivide(10, 0, byref(q))        // out param -> writes q=0, returns false
    println(if (bad) "ok=$q" else "fail")         // fail

    var x = 1
    var y = 2
    c.Swap(byref(x), byref(y))                   // ref params -> swap in place
    println("$x $y")                             // 2 1

    val v = c.Slot(1)                            // ref return WITHOUT byref -> value copy
    println(v)                                   // 20

    var slot by byref(c.Slot(1))                 // ref return via `by byref` -> live ref
    println(slot)                                // 20
    slot = 99                                    // write through the ref
    println(c.Slot(0) + c.Slot(1))               // 10 + 99 = 109

    val a = Acc()
    c.TryDivide(20, 4, byref(a.quo))             // out -> writes a.quo=5 via ldflda of its backing field
    println(a.quo)                               // 5
    c.Swap(byref(a.quo), byref(a.raw))           // ref-swap a property-backed field with a @ClrField field
    println("${a.quo} ${a.raw}")                 // 7 5
}
