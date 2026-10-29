package roundtriptests.floatingextrema

import NUnit.Framework.TestAttribute
import roundtrip.floatingextrema.*

class FloatingExtremaTests {
    @TestAttribute fun importedInlineBodiesPreserveNaNsZerosAndSingleEvaluation() {
        var calls = 0
        check(observedFloatMax(Float.NaN, 1f) { calls++; it }.isNaN())
        check(observedFloatMin(1f, Float.NaN) { calls++; it }.isNaN())
        check(observedDoubleMax(1.0, Double.NaN) { calls++; it }.isNaN())
        check(observedDoubleMin(Double.NaN, 1.0) { calls++; it }.isNaN())
        check(calls == 4)
        val f = System.BitConverter.Int32BitsToSingle(Int.MIN_VALUE)
        val d = System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE)
        check(observedFloatMax(f, 0f) { it }.toRawBits() == 0)
        check(observedFloatMin(0f, f) { it }.toRawBits() == Int.MIN_VALUE)
        check(observedDoubleMax(d, 0.0) { it }.toRawBits() == 0L)
        check(observedDoubleMin(0.0, d) { it }.toRawBits() == Long.MIN_VALUE)
    }

    @TestAttribute fun importedSavedReferencesAndVarargsKeepThePrimitiveContract() {
        val fmax = savedFloatMax(); val fmin = savedFloatMin()
        val dmax = savedDoubleMax(); val dmin = savedDoubleMin()
        check(fmax(Float.NaN, 1f).isNaN() && fmin(1f, Float.NaN).isNaN())
        check(dmax(Double.NaN, 1.0).isNaN() && dmin(1.0, Double.NaN).isNaN())
        for (index in 0..3) {
            val fs = floatArrayOf(1f, 2f, 3f, 4f); fs[index] = Float.NaN
            val ds = doubleArrayOf(1.0, 2.0, 3.0, 4.0); ds[index] = Double.NaN
            check(floatMaxMany(fs[0], fs[1], fs[2], fs[3]).isNaN())
            check(floatMinMany(fs[0], fs[1], fs[2], fs[3]).isNaN())
            check(doubleMaxMany(ds[0], ds[1], ds[2], ds[3]).isNaN())
            check(doubleMinMany(ds[0], ds[1], ds[2], ds[3]).isNaN())
        }
        val f = System.BitConverter.Int32BitsToSingle(Int.MIN_VALUE)
        val d = System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE)
        check(floatMaxMany(f, 0f, f, 0f).toRawBits() == 0)
        check(floatMinMany(0f, f, 0f, f).toRawBits() == Int.MIN_VALUE)
        check(doubleMaxMany(d, 0.0, d, 0.0).toRawBits() == 0L)
        check(doubleMinMany(0.0, d, 0.0, d).toRawBits() == Long.MIN_VALUE)
    }
}
