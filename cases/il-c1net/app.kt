// C-1 .NET consumption via facadegen (`import <ns>.<Type>`, no @Clr facade): generic methods, params/vararg, .NET
// default args, operators (op_*), struct value-type instance methods, and a C#-origin extension method. The types
// live in the referenced Probe assembly (runtime.cs); facadegen injects them from its metadata.
import Probe.Vec2
import Probe.Util
import Probe.Ext.tripled

fun main() {
    println(Util.Echo(42))            // 42   (generic method)
    println(Util.Echo("hi"))          // hi
    println(Util.Sum(1, 2, 3, 4))     // 10   (params int[])
    println(Util.AddDef(5))           // 15   (default arg b=10)
    println(Util.AddDef(5, 100))      // 105

    val c = Vec2(1, 2) + Vec2(3, 4)   // operator + (op_Addition)
    println(c.Mag2())                 // (4,6) -> 52  (struct instance method)
    println(7.tripled())              // 21   (.NET extension method)
}
