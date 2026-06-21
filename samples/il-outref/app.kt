// out/ref interop. `byref(v)` marks an argument as a .NET out/ref parameter (surfaced type ClrRef<T>); the backend
// passes the variable's address and selects the out/ref overload. `out` and `ref` unify to one `byref`. A
// ref-returning method received WITHOUT byref is a simple value copy (the managed pointer is dereferenced). (#52)
import P.Calc

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
}
