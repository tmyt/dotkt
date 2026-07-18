// #152: STRUCTURAL Double?/Float? equality (data-class equals over a value-NULLABLE float field) is TOTAL-ORDER —
// `NaN == NaN` is TRUE and `+0.0 != -0.0` — consistent with the bit-based hashCode, NOT boxed `Double.Equals` (which
// uses IEEE `==` for the value: `(-0.0).Equals(0.0)==true`). bir2cir routes a STRUCTURAL EQEQ over two raw
// `Nullable<Double/Float>` to null-safe bit-equality (nullableHasValue/nullableValue + clrDoubleEquals/clrFloatEquals):
// null==null true, one null false, both present -> total-order helper. The #95 non-null case is the twin of this.
data class D(val x: Double?)
data class F(val x: Float?)
fun main() {
    // Structural (data-class equals) over Double? — total order.
    println(D(-0.0) == D(0.0))                                  // false  (+0.0 != -0.0 structurally)
    println(D(0.0) == D(0.0))                                    // true
    println(D(Double.NaN) == D(Double.NaN))                     // true   (NaN == NaN structurally)
    println(D(null) == D(null))                                 // true   (both null)
    println(D(null) == D(0.0))                                  // false  (exactly one null)
    println(F(-0.0f) == F(0.0f))                                 // false
    println(F(Float.NaN) == F(Float.NaN))                       // true
    // hashSet consistency: equals + hashCode agree over the nullable field.
    println(hashSetOf(D(Double.NaN)).contains(D(Double.NaN)))   // true
    println(hashSetOf(D(0.0)).contains(D(-0.0)))                 // false
}
