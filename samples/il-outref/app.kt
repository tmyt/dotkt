// out/ref interop: __clrout(v)/__clrref(v) mark an argument as a .NET out/ref parameter; the backend passes the
// variable's address (byref) and selects the out/ref overload. (#52)
import P.Calc

fun main() {
    val c = Calc()
    var q = -1
    val ok = c.TryDivide(10, 2, __clrout(q))     // out: writes q=5
    println(if (ok) "ok=$q" else "fail")         // ok=5
    val bad = c.TryDivide(10, 0, __clrout(q))     // out: writes q=0, returns false
    println(if (bad) "ok=$q" else "fail")         // fail

    var x = 1
    var y = 2
    c.Swap(__clrref(x), __clrref(y))             // ref: swaps in place
    println("$x $y")                             // 2 1
}
