import ClrPInvoke.NativeMethods
import ClrPInvoke.OverloadProbeAttribute
import ClrPInvoke.ProjectionProbeAttribute
import kotlin.clr.byref

@ProjectionProbeAttribute(7, Value = 8, Values = intArrayOf(1, 2, 3))
class AttributeCarrier

@OverloadProbeAttribute(7)
class OverloadCarrier

fun main() {
    check(NativeMethods.Add(20, 22) == 42)
    var value = 9
    NativeMethods.Increment(byref(value))
    check(value == 10)
    println("DotKt consumes C# P/Invoke: OK")
}
