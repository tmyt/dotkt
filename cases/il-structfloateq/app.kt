// #95: STRUCTURAL Double/Float equality (data-class equals / hashCode) is TOTAL-ORDER — NaN==NaN is TRUE and
// +0.0 != -0.0 — consistent with the bit-based hashCode, NOT IEEE `ceq`. bir2cir routes a STRUCTURAL EQEQ over
// two Double/Float to the total-order helper (clrDoubleEquals/clrFloatEquals). A DIRECT `a == b` stays IEEE (the
// frontend emits the `ieee754equals` intrinsic for it) — the last two lines are the non-regression guard.
data class D(val x: Double)
data class F(val x: Float)
fun main() {
    // Structural (data-class equals) — total order.
    println(D(-0.0) == D(0.0))                                  // false  (+0.0 != -0.0 structurally)
    println(D(0.0) == D(0.0))                                    // true
    println(D(Double.NaN) == D(Double.NaN))                     // true   (NaN == NaN structurally)
    println(F(-0.0f) == F(0.0f))                                 // false
    println(F(Float.NaN) == F(Float.NaN))                       // true
    // hashSet consistency: equals + hashCode agree.
    println(hashSetOf(D(Double.NaN)).contains(D(Double.NaN)))   // true
    println(hashSetOf(D(0.0)).contains(D(-0.0)))                 // false
    // DIRECT comparison stays IEEE (non-regression).
    println(0.0 == -0.0)                                        // true   (IEEE: +0.0 == -0.0)
    println(Double.NaN == Double.NaN)                          // false  (IEEE: NaN != NaN)
}
