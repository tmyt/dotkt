import ClrPInvoke.NativeMethods
import kotlin.clr.byref

fun main() {
    check(NativeMethods.Add(20, 22) == 42)
    var value = 9
    NativeMethods.Increment(byref(value))
    check(value == 10)
    println("DotKt consumes C# P/Invoke: OK")
}
