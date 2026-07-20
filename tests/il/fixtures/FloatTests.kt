// Float / IEEE-arithmetic battery — migrates the floating-point/IEEE family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertTrue/assertFalse/assertEquals is strictly stronger (typed Boolean/Int, fails the exact broken contract)
// and self-documenting. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// These are the cases whose real subject is Double/Float IEEE behavior — repeatedly EXCLUDED from the String and
// numeric families because their subject is NaN/±0.0/unordered-compare/total-order/ULP, not text or coercion.
//
// EXCLUDED from this battery (matched the enumeration `ls cases/ | grep …` but the real subject is elsewhere):
//   il-fmt      -> String.format composite-format binding — String-formatting family, not IEEE (bash lane).
//   il-math     -> kotlin.math.abs/max/min/sqrt -> System.Math.* binding parity (mostly Int) — math-binding, not IEEE
//                  (now migrated to the numeric/math battery MathTests.kt).
//   il-mathabs  -> kotlin.math.abs INTEGER wraparound at Int/Long.MIN_VALUE — integer-overflow semantics, not IEEE
//                  (now migrated to MathTests.kt).
//   il-mixnum   -> mixed-type numeric coercion to the wider type (Int/Long, Int/Double) — coercion family, not IEEE
//                  (now migrated to MathTests.kt).
//   il-duration -> kotlin.time.Duration value-class member operators + toString — value-class/time family, not IEEE (bash lane).
//
// Coverage preserved (old case -> method):
//   il-nan               -> nan_comparisons            Double/Float infinities + any-NaN-compare-is-false
//   il-nancmp            -> nancmp_unorderedCompares    <=/>= use unordered cgt.un/clt.un (false on NaN); </> stay ordered
//   il-negzero           -> negzero_totalOrderVsIeee    boxed ==/compareTo is total-order (-0.0<0.0, NaN==NaN); primitive stays IEEE
//   il-structfloateq     -> structfloateq_totalOrder    STRUCTURAL data-class equality is total-order (NaN==NaN, +0.0!=-0.0)
//   il-structfloateqnull -> structfloateqnull_totalOrder STRUCTURAL nullable-field equality is null-safe total-order
//   il-floateqnull       -> floateqnull_directIeee      DIRECT/mixed nullable `==` is null-safe IEEE (single-eval)
//   il-mathnumerics      -> mathnumerics_ieeePrimitives hypot/expm1/ln1p bind the correct net10 BCL primitives
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and `Flt`/`flt`-prefixed
// to avoid clashing with sibling batteries.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.math.expm1
import kotlin.math.hypot
import kotlin.math.ln1p

// ---- il-structfloateq : STRUCTURAL Double/Float equality (data-class equals/hashCode) is total-order ----------
data class FltStructD(val x: Double)
data class FltStructF(val x: Float)

// ---- il-structfloateqnull : STRUCTURAL Double?/Float? equality (value-nullable field) is null-safe total-order -
data class FltNullStructD(val x: Double?)
data class FltNullStructF(val x: Float?)

// ---- il-floateqnull : a side-effecting nullable operand hoisted ONCE (single-evaluation guard) ----------------
var fltSideCalls = 0
fun fltSideD(): Double? { fltSideCalls++; return 1.0 }

// ---- #181 : a safe-call nullable-float operand (`c?.d == y`) — bare-local receiver, no valueBlock type stamp.
// A nullable RECEIVER over a non-null Double/Float member: the safe-call result is `Double?`/`Float?`, so the operand
// is value-nullable float and must route to the #180 null-safe IEEE path. ------------------------------------------
class FltSafeD(val d: Double)
class FltSafeF(val f: Float)

// #198: a safe-call over a VALUE-NULLABLE member (`b?.nd` where `nd: Double?`). `b.nd` is already `Nullable<Double>`,
// so `b?.nd` flattens to that same `Nullable<Double>` — kotc must NOT re-wrap it (a `newobj Nullable<T>(Nullable<T>)`
// -> InvalidProgram). This is the #181 literal repro (`class B(val d: Double?)`).
class FltSafeNND(val nd: Double?)

class FloatTests {
    // il-nan: Double/Float NaN + infinities; any comparison with NaN is false.
    @TestAttribute
    fun nan_comparisons() {
        assertTrue(Double.POSITIVE_INFINITY > 1e300)     // True
        assertTrue(Double.NEGATIVE_INFINITY < -1e300)    // True
        assertTrue(Float.POSITIVE_INFINITY > 1e30f)      // True
        assertFalse(Double.NaN > 0.0)                    // False  (any comparison with NaN is false)
        assertFalse(Double.NaN < 0.0)                    // False
    }

    // il-nancmp: `<=`/`>=` use the UNORDERED-inverted CIL compares (cgt.un/clt.un + invert) so a NaN operand
    // is false; `<`/`>` stay ordered (also false on NaN); the ordinary orderings keep working.
    @TestAttribute
    fun nancmp_unorderedCompares() {
        val n = Double.NaN
        assertFalse(n <= 1.0)        // False (a plain signed inversion wrongly returned True)
        assertFalse(n >= 1.0)        // False
        assertFalse(n < 1.0)         // False
        assertFalse(n > 1.0)         // False
        assertTrue(1.0 <= 2.0)       // True
        assertTrue(2.0 >= 2.0)       // True
        assertTrue(1.0f <= 2.0f)     // True
        assertFalse(Float.NaN <= 1.0f) // False
    }

    // il-negzero (C14): boxed `==` / `.compareTo` are TOTAL-ORDER (-0.0 < 0.0, NaN largest & NaN==NaN);
    // primitive `==` / `<` stay IEEE (-0.0 == 0.0 true, NaN == NaN false).
    @TestAttribute
    fun negzero_totalOrderVsIeee() {
        assertFalse((-0.0 as Any) == (0.0 as Any))       // False
        assertEquals(-1, (-0.0).compareTo(0.0))          // -1
        assertEquals(1, (0.0).compareTo(-0.0))           // 1
        val nan = 0.0 / 0.0
        assertTrue((nan as Any) == (nan as Any))         // True
        assertTrue(-0.0 == 0.0)                          // True  (primitive IEEE)
        assertFalse(Double.NaN == Double.NaN)            // False (primitive IEEE)
        assertEquals(1, Double.NaN.compareTo(1.0))       // 1     (NaN largest)
        assertEquals(0, Double.NaN.compareTo(Double.NaN)) // 0
        assertFalse((-0.0f as Any) == (0.0f as Any))     // False (Float)
        assertEquals(-1, (-0.0f).compareTo(0.0f))        // -1    (Float)
        assertTrue((1.0 as Any) == (1.0 as Any))         // True
    }

    // il-structfloateq (#95): STRUCTURAL Double/Float equality (data-class equals/hashCode) is total-order —
    // NaN==NaN is TRUE and +0.0 != -0.0 — NOT IEEE `ceq`. A DIRECT `a == b` stays IEEE (non-regression guard).
    @TestAttribute
    fun structfloateq_totalOrder() {
        assertFalse(FltStructD(-0.0) == FltStructD(0.0))            // False  (+0.0 != -0.0 structurally)
        assertTrue(FltStructD(0.0) == FltStructD(0.0))             // True
        assertTrue(FltStructD(Double.NaN) == FltStructD(Double.NaN)) // True   (NaN == NaN structurally)
        assertFalse(FltStructF(-0.0f) == FltStructF(0.0f))         // False
        assertTrue(FltStructF(Float.NaN) == FltStructF(Float.NaN)) // True
        // hashSet consistency: equals + hashCode agree.
        assertTrue(hashSetOf(FltStructD(Double.NaN)).contains(FltStructD(Double.NaN))) // True
        assertFalse(hashSetOf(FltStructD(0.0)).contains(FltStructD(-0.0)))             // False
        // DIRECT comparison stays IEEE (non-regression).
        assertTrue(0.0 == -0.0)                                    // True   (IEEE: +0.0 == -0.0)
        assertFalse(Double.NaN == Double.NaN)                      // False  (IEEE: NaN != NaN)
    }

    // il-structfloateqnull (#152): STRUCTURAL Double?/Float? equality (value-nullable data-class field) is
    // null-safe TOTAL-ORDER — NaN==NaN true, +0.0!=-0.0, null==null true, exactly-one-null false.
    @TestAttribute
    fun structfloateqnull_totalOrder() {
        assertFalse(FltNullStructD(-0.0) == FltNullStructD(0.0))            // False  (+0.0 != -0.0 structurally)
        assertTrue(FltNullStructD(0.0) == FltNullStructD(0.0))             // True
        assertTrue(FltNullStructD(Double.NaN) == FltNullStructD(Double.NaN)) // True   (NaN == NaN structurally)
        assertTrue(FltNullStructD(null) == FltNullStructD(null))           // True   (both null)
        assertFalse(FltNullStructD(null) == FltNullStructD(0.0))           // False  (exactly one null)
        assertFalse(FltNullStructF(-0.0f) == FltNullStructF(0.0f))         // False
        assertTrue(FltNullStructF(Float.NaN) == FltNullStructF(Float.NaN)) // True
        // hashSet consistency: equals + hashCode agree over the nullable field.
        assertTrue(hashSetOf(FltNullStructD(Double.NaN)).contains(FltNullStructD(Double.NaN))) // True
        assertFalse(hashSetOf(FltNullStructD(0.0)).contains(FltNullStructD(-0.0)))             // False
    }

    // il-floateqnull (#180): DIRECT/mixed nullable Double?/Float? `==` routes to the ieee754equals intrinsic with
    // raw Nullable<T> operands — null-safe IEEE (null==null true, one null false, both present -> IEEE value ==),
    // incl. `(x as Double?) == y` (SURFACE nullness), `!=`, and single-evaluation of a side-effecting operand.
    @TestAttribute
    fun floateqnull_directIeee() {
        val negZero: Double? = -0.0
        val posZero: Double? = 0.0
        val nanD: Double? = Double.NaN
        val nullD: Double? = null
        val oneD: Double? = 1.0

        // Double? == Double? (both nullable) — IEEE.
        assertTrue(negZero == posZero)   // True   (IEEE: -0.0 == 0.0)
        assertFalse(nanD == nanD)        // False  (IEEE: NaN != NaN)
        assertTrue(nullD == nullD)       // True   (both null)
        assertFalse(oneD == nullD)       // False  (exactly one null)
        assertFalse(oneD == posZero)     // False  (both present, distinct)

        // Double == Double? (mixed) — IEEE when the nullable side is non-null, false when it is null.
        assertTrue(1.0 == oneD)          // True
        assertFalse(2.0 == oneD)         // False
        assertFalse(nullD == 1.0)        // False  (mixed, left null)

        // (x as Double?) == Double? — an explicit cast-to-nullable over a boxed Any: nullness from the SURFACE type.
        val boxed: Any = 0.0
        assertTrue((boxed as Double?) == posZero)   // True   (0.0 == 0.0)
        assertFalse((boxed as Double?) == oneD)     // False  (0.0 != 1.0)

        // `!=` (negated ieee754equals) over nullable operands.
        assertTrue(oneD != posZero)      // True   (1.0 != 0.0)
        assertTrue(nanD != nanD)         // True   (NaN != NaN)

        // Single-evaluation: a side-effecting operand is hoisted once, not re-read per HasValue/Value.
        fltSideCalls = 0
        assertTrue(fltSideD() == oneD)   // True
        assertEquals(1, fltSideCalls)    // 1

        // Float? twin.
        val negZeroF: Float? = -0.0f
        val posZeroF: Float? = 0.0f
        val nanF: Float? = Float.NaN
        val nullF: Float? = null
        val oneF: Float? = 1.0f
        assertTrue(negZeroF == posZeroF) // True   (IEEE: -0.0f == 0.0f)
        assertFalse(nanF == nanF)        // False  (IEEE: NaN != NaN)
        assertTrue(nullF == nullF)       // True   (both null)
        assertTrue(1.0f == oneF)         // True   (mixed Float == Float?)
        assertFalse(nullF == 2.0f)       // False  (mixed, left null)
    }

    // #181: a safe-call nullable-float operand (`c?.d == y`) with a BARE-LOCAL nullable receiver — kotc emits the
    // safe-call as a raw `cond` (nullableWrap/nullableNull arms) with NO valueBlock `type` stamp, so bir2cir's
    // StaticType.Surface must recover the value-nullable `Double?`/`Float?` surface from those arms to route the
    // ieee754equals to the #180 null-safe path (else a raw `ceq` over `Nullable<T>` — unverifiable IL). A null
    // receiver makes `c?.d` null -> exactly-one-null -> FALSE; a present receiver compares by IEEE (NaN != NaN,
    // -0.0 == 0.0).
    @TestAttribute
    fun safecall_nullableFloat() {
        val someD: FltSafeD? = FltSafeD(1.0)
        val zeroD: FltSafeD? = FltSafeD(-0.0)
        val nanD: FltSafeD? = FltSafeD(Double.NaN)
        val nullRecvD: FltSafeD? = null

        assertTrue(someD?.d == 1.0)        // True   (present -> IEEE ==)
        assertFalse(someD?.d == 2.0)       // False  (present, distinct)
        assertTrue(zeroD?.d == 0.0)        // True   (IEEE: -0.0 == 0.0)
        assertFalse(nanD?.d == Double.NaN) // False  (IEEE: NaN != NaN)
        assertFalse(nullRecvD?.d == 1.0)   // False  (receiver null -> c?.d null -> one null)
        // `!=` twin.
        assertTrue(someD?.d != 2.0)        // True
        assertTrue(nullRecvD?.d != 1.0)    // True   (null != 1.0)
        // Concat over the same value-nullable safe-call surface: a null receiver renders "null" (the Surface change
        // routes the bare-local safe-call part through the null-safe LibraryKt.toString, not a raw Nullable<T> part).
        assertTrue("${nullRecvD?.d}" == "null")   // True

        // Float? twin.
        val someF: FltSafeF? = FltSafeF(1.0f)
        val nanF: FltSafeF? = FltSafeF(Float.NaN)
        val nullRecvF: FltSafeF? = null
        assertTrue(someF?.f == 1.0f)       // True
        assertFalse(nanF?.f == Float.NaN)  // False  (IEEE: NaN != NaN)
        assertFalse(nullRecvF?.f == 1.0f)  // False  (receiver null)

        // #198: safe-call over a VALUE-NULLABLE member — `b?.nd` (nd: Double?) must NOT double-wrap the already-
        // Nullable<Double> member (#181 literal repro). Three shapes: present receiver + present value, present
        // receiver + null value, null receiver.
        val presentD: FltSafeNND? = FltSafeNND(3.0)
        val innerNullD: FltSafeNND? = FltSafeNND(null)
        val nullRecvNND: FltSafeNND? = null
        assertTrue(presentD?.nd == 3.0)    // True   (receiver present, member present)
        assertFalse(presentD?.nd == 4.0)   // False  (present, distinct)
        assertTrue(innerNullD?.nd == null) // True   (receiver present, member null)
        assertTrue(nullRecvNND?.nd == null)// True   (receiver null -> flattened null)
    }

    // il-mathnumerics (#141): hypot/expm1/ln1p must bind the numerically-correct net10 BCL primitives
    // (System.Double/Single.Hypot/ExpM1/LogP1), not the overflow-prone sqrt(x*x+y*y) / cancellation-prone
    // exp(x)-1 / ln(1+x) pure-Kotlin forms.
    @TestAttribute
    fun mathnumerics_ieeePrimitives() {
        assertEquals(5.0, hypot(3.0, 4.0))            // 5.0
        assertTrue(hypot(1e308, 1e308).isFinite())    // True  (buggy sqrt path -> Infinity -> false)
        assertEquals(0.0, expm1(0.0))                 // 0.0
        assertTrue(expm1(1e-15) > 5e-16)              // True  (correct ~1e-15; exp(x)-1 -> ~1.1e-16 -> false)
        assertEquals(0.0, ln1p(0.0))                  // 0.0
        assertTrue(ln1p(1e-15) > 5e-16)               // True  (correct ~1e-15; ln(1+x) -> ~1.1e-16 -> false)
        assertEquals(5.0f, hypot(3.0f, 4.0f))         // 5.0 (Float)
        assertTrue(hypot(1e30f, 1e30f).isFinite())    // True  (Float sqrt path overflows -> false)
    }
}
