// #180: DIRECT/mixed nullable float `==` (`Double? == Double?`, `Double == Double?`, `(x as Double?) == y`). The
// frontend routes these to the `ieee754equals` intrinsic with RAW `Nullable<T>` operands; a raw `binOp ==` would emit
// `ceq` over `Nullable<double>` structs = unverifiable IL / InvalidProgram. bir2cir shapes a null-safe IEEE compare
// (operand-hoist skeleton, RAW `binOp ==` core, nullness detected off the SURFACE static type so an `as Double?` cast
// still counts): null==null true, one null false, both present -> IEEE `==` on the values. Per the Kotlin spec
// (value-equality), IEEE applies when both compile-time types are floating-point OR their nullable variants, so
// `-0.0 == 0.0` is TRUE and `NaN == NaN` is FALSE even when nullable — the direct-operator semantics of #95 (non-null)
// and DISTINCT from the STRUCTURAL total-order #152 path (data-class `equals` over a `Double?` field).
var sideCalls = 0
fun sideD(): Double? { sideCalls++; return 1.0 }
fun main() {
    val negZero: Double? = -0.0
    val posZero: Double? = 0.0
    val nanD: Double? = Double.NaN
    val nullD: Double? = null
    val oneD: Double? = 1.0

    // Double? == Double? (both nullable) — IEEE.
    println(negZero == posZero)   // True   (IEEE: -0.0 == 0.0)
    println(nanD == nanD)         // False  (IEEE: NaN != NaN)
    println(nullD == nullD)       // True   (both null)
    println(oneD == nullD)        // False  (exactly one null)
    println(oneD == posZero)      // False  (both present, distinct)

    // Double == Double? (mixed) — IEEE when the nullable side is non-null, false when it is null.
    println(1.0 == oneD)          // True
    println(2.0 == oneD)          // False
    println(nullD == 1.0)         // False  (mixed, left null)

    // (x as Double?) == Double? — an explicit cast-to-nullable over a boxed Any: nullness comes from the SURFACE type.
    val boxed: Any = 0.0
    println((boxed as Double?) == posZero)   // True   (0.0 == 0.0)
    println((boxed as Double?) == oneD)      // False  (0.0 != 1.0)

    // `!=` (negated ieee754equals) over nullable operands.
    println(oneD != posZero)      // True   (1.0 != 0.0)
    println(nanD != nanD)         // True   (NaN != NaN)

    // Single-evaluation: a side-effecting operand is hoisted once, not re-read per HasValue/Value.
    println(sideD() == oneD)      // True
    println(sideCalls)            // 1

    // Float? twin.
    val negZeroF: Float? = -0.0f
    val posZeroF: Float? = 0.0f
    val nanF: Float? = Float.NaN
    val nullF: Float? = null
    val oneF: Float? = 1.0f
    println(negZeroF == posZeroF) // True   (IEEE: -0.0f == 0.0f)
    println(nanF == nanF)         // False  (IEEE: NaN != NaN)
    println(nullF == nullF)       // True   (both null)
    println(1.0f == oneF)         // True   (mixed Float == Float?)
    println(nullF == 2.0f)        // False  (mixed, left null)
}
