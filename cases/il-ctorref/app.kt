// Constructor references `::Ctor` and lambdas/refs whose delegate signature mentions a USER class — both go
// through `Func<…, UserType>`, resolved via TypeBuilder.GetConstructor/GetMethod (the generic-over-TypeBuilder
// bridge). Previously this hit the Reflection.Emit "does not support resolving members" limit.
class Point(val x: Int, val y: Int) { fun show(): String = "($x,$y)" }

fun build(f: (Int, Int) -> Point): Point = f(3, 4)
fun makeWith(f: (Int) -> Point): String = f(9).show()

fun main() {
    val mk = ::Point                              // constructor reference stored in a val
    println(mk(1, 2).show())                       // (1,2)
    println(build(::Point).show())                 // (3,4)  (::Ctor as a higher-order arg)
    println(makeWith { n -> Point(n, n) })         // (9,9)  (lambda returning a user type)
}
