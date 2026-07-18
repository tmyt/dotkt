// #141: hypot/expm1/ln1p must bind the numerically-correct net10 BCL primitives
// (System.Double/Single.Hypot/ExpM1/LogP1), not the overflow-prone sqrt(x*x+y*y)
// and cancellation-prone exp(x)-1 / ln(1+x) pure-Kotlin forms.
import kotlin.math.*

fun main() {
    println(hypot(3.0, 4.0))                 // 5.0
    println(hypot(1e308, 1e308).isFinite())  // true  (buggy sqrt path -> Infinity -> false)
    println(expm1(0.0))                       // 0.0
    println(expm1(1e-15) > 5e-16)             // true  (correct ~1e-15; exp(x)-1 -> ~1.1e-16 -> false)
    println(ln1p(0.0))                         // 0.0
    println(ln1p(1e-15) > 5e-16)              // true  (correct ~1e-15; ln(1+x) -> ~1.1e-16 -> false)
    println(hypot(3.0f, 4.0f))               // 5.0
    println(hypot(1e30f, 1e30f).isFinite())  // true  (Float sqrt path overflows -> false)
}
