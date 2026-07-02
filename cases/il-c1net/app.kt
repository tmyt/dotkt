// C-1 .NET consumption via facadegen (`import <ns>.<Type>`, no @Clr facade): generic methods, params/vararg, .NET
// default args, operators (op_*), struct value-type instance methods, and a C#-origin extension method. The types
// live in the referenced Probe assembly (runtime.cs); facadegen injects them from its metadata.
import Probe.Vec2
import Probe.Util
import Probe.Ext.tripled
import Probe.Ext.shout

fun main() {
    println(Util.Echo(42))            // 42   (generic method)
    println(Util.Echo("hi"))          // hi
    println(Util.Sum(1, 2, 3, 4))     // 10   (params int[])
    println(Util.AddDef(5))           // 15   (default arg b=10)
    println(Util.AddDef(5, 100))      // 105

    val c = Vec2(1, 2) + Vec2(3, 4)   // operator + (op_Addition)
    println(c.Mag2())                 // (4,6) -> 52  (struct instance method)
    println(7.tripled())              // 21   (.NET extension method, Int receiver)

    // The rest of the op_* battery (op_Subtraction/op_Multiply/op_Division/op_UnaryNegation). Note:
    // op_Equality/op_Inequality are deliberately NOT mapped — Kotlin `==` routes to Equals(Any?), the
    // correct Kotlin semantics; op_Implicit/op_Explicit have no Kotlin analog and are skipped.
    println((Vec2(5, 7) - Vec2(1, 2)).Mag2())  // (4,5)  -> 41
    println((Vec2(2, 3) * 3).Mag2())           // (6,9)  -> 117
    println((Vec2(8, 4) / 2).Mag2())           // (4,2)  -> 20
    println((-Vec2(1, 2)).Mag2())              // (-1,-2)-> 5
    println("yo".shout())                      // yo!   (.NET extension method, String receiver)
}
