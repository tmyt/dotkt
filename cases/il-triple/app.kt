// COV4 (kcc review §2B): Triple coverage (il-pair only covered Pair). Construction, .first/.second/.third,
// destructuring, componentN, copy (all args explicit), structural equality, and toString — pure Kotlin,
// JVM-oracle-comparable.
//
// DELIBERATELY OMITTED — Triple.copy(second = "TWO") (a PARTIAL copy relying on the stdlib-generated
// default parameter values): it InvalidProgram-crashes on the CLR. This is the tracked cross-module
// default-argument bug (MEMORY cross-module-default-args-not-preserved: the frontend jar drops the
// stdlib data class's default VALUES, so a `copy` that omits any argument emits invalid IL). Routed
// layer = kotc/bir2cir default-value preservation / $default synthetics — NOT this test's concern.
// Full-argument copy (below) works, so it IS gated; the partial form waits on that fix.

fun swapEnds(t: Triple<Int, String, Int>): Triple<Int, String, Int> =
    t.copy(t.third, t.second, t.first)   // all args explicit (no defaults)

fun main() {
    val t = Triple(1, "two", 3)
    println(t.first)                 // 1
    println(t.second)                // two
    println(t.third)                 // 3
    println(t.toString())            // (1, two, 3)
    println(t)                       // (1, two, 3)

    val (a, b, c) = t                // destructuring
    println("$a|$b|$c")             // 1|two|3

    println(t.component1())          // 1
    println(t.component2())          // two
    println(t.component3())          // 3

    val u = t.copy(1, "TWO", 3)      // full-arg copy
    println(u)                       // (1, TWO, 3)
    println(t == Triple(1, "two", 3))// true (structural equality)
    println(u == t)                  // false

    val s = swapEnds(t)              // Triple across a function boundary + copy
    println(s)                       // (3, two, 1)

    val nested = Triple(listOf(1, 2), "x", mapOf("k" to 9))
    println(nested)                  // ([1, 2], x, {k=9})
}
