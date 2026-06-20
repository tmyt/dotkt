// C-1 .NET consumption: generic methods, params, .NET default args, operators, struct value-type methods.
import clr.Vec2
import clr.Util

fun main() {
    println(Util.echo(42))            // 42   (generic method)
    println(Util.echo("hi"))          // hi
    println(Util.sum(1, 2, 3, 4))     // 10   (params int[])
    println(Util.addDef(5))           // 15   (default arg b=10)
    println(Util.addDef(5, 100))      // 105

    val c = Vec2(1, 2) + Vec2(3, 4)   // operator + (op_Addition)
    println(c.mag2())                 // (4,6) -> 52  (struct instance method)
}
