// COV3 (kcc review §2B): `typealias` coverage. Aliases over a stdlib generic (List<String>), a function
// type ((Int)->Int), and a user class — each USED ACROSS A FUNCTION BOUNDARY (param + return types), which
// is where an alias that isn't fully expanded would surface. Pure Kotlin -> JVM-oracle-comparable.

typealias Names = List<String>
typealias IntOp = (Int) -> Int
typealias Pairs = Map<String, Int>

class Box(val v: Int) { fun twice(): Int = v * 2 }
typealias Container = Box

fun join(ns: Names): String = ns.joinToString(",")   // alias in PARAMETER position

fun makeNames(): Names = listOf("a", "b", "c")        // alias in RETURN position

fun apply2(op: IntOp, x: Int): Int = op(op(x))        // function-type alias across the boundary

fun unwrap(c: Container): Int = c.twice()             // user-class alias across the boundary

fun lookup(p: Pairs, k: String): Int = p[k] ?: -1     // alias over a stdlib Map

fun main() {
    val ns: Names = makeNames()
    println(join(ns))                                 // a,b,c
    println(ns.size)                                  // 3

    val inc: IntOp = { it + 1 }
    println(apply2(inc, 10))                          // 12

    println(unwrap(Container(21)))                    // 42

    val p: Pairs = mapOf("x" to 7, "y" to 9)
    println(lookup(p, "y"))                           // 9
    println(lookup(p, "z"))                           // -1
}
