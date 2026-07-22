// Math / numeric-arithmetic battery — migrates the numeric-operation family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals/assertTrue/assertFalse is strictly stronger (typed Int/Long/Double/Boolean, fails the exact broken
// contract) than the old text diff. Every value the old il_check / differential oracle asserted is preserved 1:1
// (see the `// <expected>` comments).
//
// This is the numeric/math family the FloatTests battery explicitly DEFERRED (its header excludes il-math,
// il-mathabs, il-mixnum as "math-binding / integer-overflow / coercion, NOT IEEE"). Their real subject is
// kotlin.math.* -> System.Math.* binding, integer overflow/wraparound, coercion, and integer div/rem wrap — not
// floating-point IEEE edge behavior — so they land here, not in FloatTests.
//
// Coverage preserved (old case -> method):
//   il-math        -> math_binding          kotlin.math abs/max/min/sqrt -> System.Math.* (clrStatic lowering)
//   il-mathabs     -> mathabs_integerWrap   #C9 kotlin.math.abs WRAPS at Int/Long.MIN_VALUE (unchecked neg, no throw)
//   il-coerce      -> coerce_clamp          coerceAtMost/AtLeast/In (value + progression-range) pure-Kotlin bodies
//   il-roundhalfup -> roundHalfUp_towardPosInf  #103 roundToInt/roundToLong = round-half-UP (floor(x+0.5)); NaN throws; saturates
//   il-divmin      -> divmin_wrap           #C10 Int/Long MIN_VALUE / -1 and % -1 WRAP (not OverflowException); trunc-toward-zero
//   il-mixnum      -> mixnum_widerType      mixed-type numeric arithmetic coerces to the wider type (Double/Int, Int/Long)
//
// il-divmin and il-mixnum carried NO il_check golden (differential-PURE only); their JVM-oracle contract is exactly
// a set of numeric values, so it is captured 1:1 here as typed value asserts (design D3: value asserts subsume the
// oracle) and their verify-differential.sh PURE entries are removed in the SAME change.
//
// Top-level names are unique within this single battery assembly (one project = one namespace); every method body is
// self-contained (no shared top-level declarations), so there is nothing to prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt
import kotlin.math.roundToInt
import kotlin.math.roundToLong

class MathTests {
    // il-math: kotlin.math.* -> System.Math.* clrStatic lowering parity.
    @TestAttribute
    fun binding() {
        assertEquals(9, abs(-9))       // 9
        assertEquals(7, max(3, 7))     // 7
        assertEquals(3, min(3, 7))     // 3
        assertEquals(4.0, sqrt(16.0))  // 4  (Double)
    }

    // il-mathabs (C9): kotlin.math.abs WRAPS at MIN_VALUE (unchecked negation) — it does NOT throw like
    // System.Math.Abs's checked overload. abs(Int.MIN_VALUE) == Int.MIN_VALUE, abs(Long.MIN_VALUE) == Long.MIN_VALUE.
    @TestAttribute
    fun integerWrap() {
        assertEquals(Int.MIN_VALUE, abs(Int.MIN_VALUE))    // -2147483648  (wraps, no throw)
        assertEquals(Long.MIN_VALUE, abs(Long.MIN_VALUE))  // -9223372036854775808  (wraps, no throw)
        assertEquals(5, abs(-5))                           // 5
        assertEquals(5L, abs(-5L))                         // 5  (Long)
        assertEquals(0, abs(0))                            // 0
        assertEquals(Int.MAX_VALUE, abs(Int.MAX_VALUE))    // 2147483647
    }

    // il-coerce: coerceAtMost/coerceAtLeast/coerceIn run the pure-Kotlin stdlib bodies (NO kotc System.Math lowering).
    @TestAttribute
    fun clamp() {
        assertEquals(7, 10.coerceAtMost(7))    // 7
        assertEquals(5, 3.coerceAtLeast(5))    // 5
        assertEquals(5, 8.coerceIn(1, 5))      // 5
        assertEquals(2, 2.coerceIn(1, 5))      // 2
        assertEquals(1, 0.coerceIn(1, 5))      // 1
        assertEquals(5, 8.coerceIn(1..5))      // 5  (progression-range form)
        assertEquals(7L, 7L.coerceAtMost(10L)) // 7  (Long)
    }

    // il-roundhalfup (#103): Double/Float.roundToInt/roundToLong are round-half-UP toward +inf (floor(x+0.5)), NOT
    // banker's rounding (ToEven). Ties: 0.5->1, 2.5->3, -2.5->-2, -0.5->0. NaN throws IllegalArgumentException;
    // out-of-range saturates to Int/Long MIN/MAX.
    @TestAttribute
    fun towardPosInf() {
        assertEquals(3, 2.5.roundToInt())        // 3
        assertEquals(-2, (-2.5).roundToInt())    // -2
        assertEquals(1, 0.5.roundToInt())        // 1
        assertEquals(0, (-0.5).roundToInt())     // 0
        assertEquals(4, 3.5.roundToInt())        // 4
        assertEquals(2, 2.4.roundToInt())        // 2  (not a tie)
        assertEquals(3, 2.6.roundToInt())        // 3
        assertEquals(3L, 2.5.roundToLong())      // 3  (Long)
        assertEquals(-2L, (-2.5).roundToLong())  // -2 (Long)
        assertEquals(3, 2.5f.roundToInt())       // 3  (Float)
        assertEquals(-2, (-2.5f).roundToInt())   // -2 (Float)
        assertEquals(3L, 2.5f.roundToLong())     // 3  (Float -> Long)
        assertEquals(2147483647, 1e30.roundToInt())     // Int.MAX_VALUE  (saturates)
        assertEquals(-2147483648, (-1e30).roundToInt()) // Int.MIN_VALUE  (saturates)
        var nanThrew = false
        try { Double.NaN.roundToInt() } catch (e: IllegalArgumentException) { nanThrew = true }
        assertTrue(nanThrew)  // NaN-throws
    }

    // il-divmin (C10): Int/Long MIN_VALUE / -1 and % -1 must WRAP (Kotlin), not throw OverflowException (raw CIL
    // div/rem). Integer division truncates toward zero; the remainder takes the dividend's sign.
    @TestAttribute
    fun wrap() {
        assertEquals(Int.MIN_VALUE, Int.MIN_VALUE / -1)    // -2147483648  (wraps)
        assertEquals(0, Int.MIN_VALUE % -1)                // 0
        assertEquals(Long.MIN_VALUE, Long.MIN_VALUE / -1L) // -9223372036854775808
        assertEquals(0L, Long.MIN_VALUE % -1L)             // 0
        assertEquals(-7, 7 / -1)                           // -7
        assertEquals(0, 7 % -1)                            // 0
        assertEquals(-3, -13 / 4)                          // -3  (truncates toward zero)
        assertEquals(-1, -13 % 4)                          // -1  (sign of dividend)
        assertEquals(3, 10 / 3)                            // 3
        assertEquals(1, 10 % 3)                            // 1
        val a = -2147483648; val b = -1
        assertEquals(-2147483648, a / b)                   // -2147483648  (variable path)
        assertEquals(0, a % b)                             // 0
    }

    // il-mixnum: mixed-type numeric arithmetic coerces to the wider type (Double/Int, Int/Long, comparisons).
    @TestAttribute
    fun widerType() {
        val d = 8.0; val n = 3
        assertEquals(2.6666666666666665, d / n)  // 2.6666666666666665  (Int widened to Double)
        assertEquals(14.0, n * 2 + d)            // 14.0
        assertTrue(d > n)                        // true (Int widened for the compare)
        val i = 5; val l = 10L
        assertEquals(15L, i + l)                 // 15  (Int widened to Long)
        assertEquals(5L, l - i)                  // 5   (Long)
        assertEquals(3, 10 / 3)                  // 3   (Int division)
        assertEquals(3.5, 7.0 / 2.0)             // 3.5 (Double)
    }
}
