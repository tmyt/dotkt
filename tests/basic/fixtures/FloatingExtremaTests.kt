import NUnit.Framework.TestAttribute

private class FloatingExtremaToken(val rank: Int) : Comparable<FloatingExtremaToken> {
    override fun compareTo(other: FloatingExtremaToken): Int = rank.compareTo(other.rank)
}

class FloatingExtremaTests {
    @TestAttribute fun floatNaNsSurviveEveryPairTripleAndVarargPosition() {
        check(kotlin.math.max(Float.NaN, 1f).isNaN() && kotlin.math.min(Float.NaN, 1f).isNaN())
        check(Float.NaN.compareTo(1f) > 0)
        for (index in 0..1) {
            val values = floatArrayOf(1f, 2f)
            values[index] = Float.NaN
            check(maxOf(values[0], values[1]).isNaN())
            check(minOf(values[0], values[1]).isNaN())
        }
        for (index in 0..2) {
            val values = floatArrayOf(1f, 2f, 3f)
            values[index] = Float.NaN
            check(maxOf(values[0], values[1], values[2]).isNaN())
            check(minOf(values[0], values[1], values[2]).isNaN())
        }
        for (index in 0..3) {
            val values = floatArrayOf(1f, 2f, 3f, 4f)
            values[index] = Float.NaN
            check(maxOf(values[0], values[1], values[2], values[3]).isNaN())
            check(minOf(values[0], values[1], values[2], values[3]).isNaN())
        }
        check(maxOf(Float.NaN, Float.NaN).isNaN() && minOf(Float.NaN, Float.NaN).isNaN())
    }

    @TestAttribute fun doubleNaNsSurviveEveryPairTripleAndVarargPosition() {
        check(kotlin.math.max(Double.NaN, 1.0).isNaN() && kotlin.math.min(Double.NaN, 1.0).isNaN())
        check(Double.NaN.compareTo(1.0) > 0)
        for (index in 0..1) {
            val values = doubleArrayOf(1.0, 2.0)
            values[index] = Double.NaN
            check(maxOf(values[0], values[1]).isNaN())
            check(minOf(values[0], values[1]).isNaN())
        }
        for (index in 0..2) {
            val values = doubleArrayOf(1.0, 2.0, 3.0)
            values[index] = Double.NaN
            check(maxOf(values[0], values[1], values[2]).isNaN())
            check(minOf(values[0], values[1], values[2]).isNaN())
        }
        for (index in 0..3) {
            val values = doubleArrayOf(1.0, 2.0, 3.0, 4.0)
            values[index] = Double.NaN
            check(maxOf(values[0], values[1], values[2], values[3]).isNaN())
            check(minOf(values[0], values[1], values[2], values[3]).isNaN())
        }
        check(maxOf(Double.NaN, Double.NaN).isNaN() && minOf(Double.NaN, Double.NaN).isNaN())
    }

    @TestAttribute fun signedZeroOrderingIsIndependentOfOperandOrder() {
        val f = System.BitConverter.Int32BitsToSingle(Int.MIN_VALUE)
        val d = System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE)
        for (a in floatArrayOf(0f, f)) for (b in floatArrayOf(0f, f)) {
            val expectedMax = if (a.toRawBits() == 0 || b.toRawBits() == 0) 0 else Int.MIN_VALUE
            val expectedMin = if (a.toRawBits() == Int.MIN_VALUE || b.toRawBits() == Int.MIN_VALUE) Int.MIN_VALUE else 0
            check(maxOf(a, b).toRawBits() == expectedMax)
            check(minOf(a, b).toRawBits() == expectedMin)
        }
        for (a in doubleArrayOf(0.0, d)) for (b in doubleArrayOf(0.0, d)) {
            val expectedMax = if (a.toRawBits() == 0L || b.toRawBits() == 0L) 0L else Long.MIN_VALUE
            val expectedMin = if (a.toRawBits() == Long.MIN_VALUE || b.toRawBits() == Long.MIN_VALUE) Long.MIN_VALUE else 0L
            check(maxOf(a, b).toRawBits() == expectedMax)
            check(minOf(a, b).toRawBits() == expectedMin)
        }
        for (index in 0..2) {
            val fs = floatArrayOf(0f, 0f, 0f); fs[index] = f
            val ds = doubleArrayOf(0.0, 0.0, 0.0); ds[index] = d
            check(minOf(fs[0], fs[1], fs[2]).toRawBits() == Int.MIN_VALUE)
            check(maxOf(fs[0], fs[1], fs[2]).toRawBits() == 0)
            check(minOf(ds[0], ds[1], ds[2]).toRawBits() == Long.MIN_VALUE)
            check(maxOf(ds[0], ds[1], ds[2]).toRawBits() == 0L)
        }
        for (index in 0..3) {
            val fs = floatArrayOf(0f, 0f, 0f, 0f); fs[index] = f
            val ds = doubleArrayOf(0.0, 0.0, 0.0, 0.0); ds[index] = d
            check(minOf(fs[0], fs[1], fs[2], fs[3]).toRawBits() == Int.MIN_VALUE)
            check(maxOf(fs[0], fs[1], fs[2], fs[3]).toRawBits() == 0)
            check(minOf(ds[0], ds[1], ds[2], ds[3]).toRawBits() == Long.MIN_VALUE)
            check(maxOf(ds[0], ds[1], ds[2], ds[3]).toRawBits() == 0L)
        }
    }

    @TestAttribute fun finiteInfinityEmptyAndCallableReferenceControls() {
        check(maxOf(-3f, 2f) == 2f && minOf(-3f, 2f) == -3f)
        check(maxOf(-3.0, 2.0, 1.0) == 2.0 && minOf(-3.0, 2.0, 1.0) == -3.0)
        check(maxOf(Float.NEGATIVE_INFINITY, 1f, Float.POSITIVE_INFINITY) == Float.POSITIVE_INFINITY)
        check(minOf(Double.POSITIVE_INFINITY, 1.0, Double.NEGATIVE_INFINITY) == Double.NEGATIVE_INFINITY)
        check(maxOf(System.BitConverter.Int32BitsToSingle(Int.MIN_VALUE), *floatArrayOf()).toRawBits() == Int.MIN_VALUE)
        check(minOf(0f, *floatArrayOf()).toRawBits() == 0)
        check(maxOf(System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE), *doubleArrayOf()).toRawBits() == Long.MIN_VALUE)
        check(minOf(0.0, *doubleArrayOf()).toRawBits() == 0L)
        val floatMax: (Float, Float) -> Float = ::maxOf
        val floatMin: (Float, Float) -> Float = ::minOf
        val doubleMax: (Double, Double) -> Double = ::maxOf
        val doubleMin: (Double, Double) -> Double = ::minOf
        check(floatMax(Float.NaN, 1f).isNaN() && floatMin(Float.NaN, 1f).isNaN())
        check(doubleMax(Double.NaN, 1.0).isNaN() && doubleMin(Double.NaN, 1.0).isNaN())
    }

    @TestAttribute fun genericComparableKeepsTieIdentityAndTotalOrder() {
        val first = FloatingExtremaToken(1)
        val second = FloatingExtremaToken(1)
        val third = FloatingExtremaToken(1)
        check(maxOf(first, second) === first && minOf(first, second) === first)
        check(maxOf(first, second, third) === first && minOf(first, second, third) === first)
        check(maxOf(first, second, third, first) === first && minOf(first, second, third, first) === first)
        check(maxOf(first, FloatingExtremaToken(2)).rank == 2)
        check(minOf<Double>(Double.NaN, 1.0) == 1.0)
        check(minOf<Float>(Float.NaN, 1f) == 1f)
        check(maxOf<Double>(Double.NaN, 1.0).isNaN())
        check(maxOf<Float>(Float.NaN, 1f).isNaN())
    }
}
