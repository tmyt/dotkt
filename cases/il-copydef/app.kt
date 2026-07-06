// COV (kcc review Part 2 C3, 2026-07-06): a data-class `copy(field = x)` with the OTHER fields omitted.
// A data-class copy default is always `this.<field>`; the frontend jar drops that VALUE cross-module
// (IrErrorExpression), so a partial `Pair`/`Triple.copy` used to place the named arg in the wrong slot
// (`(1 to 2).copy(second = 20)` -> `(20, 0)`) or InvalidProgram. kotc now reconstructs each omitted
// copy field as a receiver field read at the instantiated call site (Pair/Triple = the STDLIB data-class
// analogue of the same-module user path). JVM-oracle PURE.
data class Point(val x: Int, val y: Int, val z: Int)

fun main() {
    println((1 to 2).copy(second = 20))                  // (1, 20)  — cross-module Pair, tail field omitted
    println((1 to 2).copy(first = 5))                    // (5, 2)   — cross-module Pair, lead field omitted
    println(Triple(1, 2, 3).copy(second = 9))            // (1, 9, 3) — cross-module Triple, middle field
    println(Triple(1, 2, 3).copy(first = 7, third = 8))  // (7, 2, 8) — two provided, middle omitted
    val p = Point(1, 2, 3)
    println(p.copy(y = 20))                              // Point(x=1, y=20, z=3) — same-module user data class
    println(p.copy(x = 9, z = 8))                        // Point(x=9, y=2, z=8)
}
