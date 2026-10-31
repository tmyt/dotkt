import NUnit.Framework.TestAttribute
import kotlin.math.*

private fun sameNumericDouble(actual: Double, expected: Double) {
    if (expected.isNaN()) check(actual.isNaN()) else check(actual.toRawBits() == expected.toRawBits())
}

private fun sameNumericFloat(actual: Float, expected: Float) {
    if (expected.isNaN()) check(actual.isNaN()) else check(actual.toRawBits() == expected.toRawBits())
}

class PortableNumericTests {
    @TestAttribute fun classificationsKeepIeeeCategoriesAndCanonicalNaNBits() {
        for (value in doubleArrayOf(0.0, -0.0, Double.MIN_VALUE, -Double.MIN_VALUE, Double.MAX_VALUE, -Double.MAX_VALUE, 1.0, -1.0)) {
            check(value.isFinite() && !value.isNaN() && !value.isInfinite())
        }
        for (value in doubleArrayOf(Double.POSITIVE_INFINITY, Double.NEGATIVE_INFINITY)) {
            check(value.isInfinite() && !value.isFinite() && !value.isNaN())
        }
        for (bits in longArrayOf(0x7ff8000000000001L, 0x7ff0000000000001L, Long.MIN_VALUE or 0x7ff8000000000042L)) {
            val value = Double.fromBits(bits)
            check(value.isNaN() && !value.isFinite() && !value.isInfinite())
            check(value.toBits() == 0x7ff8000000000000L)
        }
        for (value in floatArrayOf(0.0f, -0.0f, Float.MIN_VALUE, -Float.MIN_VALUE, Float.MAX_VALUE, -Float.MAX_VALUE, 1.0f, -1.0f)) {
            check(value.isFinite() && !value.isNaN() && !value.isInfinite())
        }
        for (value in floatArrayOf(Float.POSITIVE_INFINITY, Float.NEGATIVE_INFINITY)) {
            check(value.isInfinite() && !value.isFinite() && !value.isNaN())
        }
        for (bits in intArrayOf(0x7fc00001, 0x7f800001, Int.MIN_VALUE or 0x7fc00042)) {
            val value = Float.fromBits(bits)
            check(value.isNaN() && !value.isFinite() && !value.isInfinite())
            check(value.toBits() == 0x7fc00000)
        }
    }

    @TestAttribute fun rotationsAndOneBitSelectionsKeepEveryBitAndMaskDistances() {
        val distances = intArrayOf(Int.MIN_VALUE, -129, -65, -33, -32, -1, 0, 1, 7, 31, 32, 33, 63, 64, 65, Int.MAX_VALUE)
        val values = intArrayOf(0, 1, -1, Int.MIN_VALUE, Int.MAX_VALUE, 0x12345678)
        val highest = intArrayOf(0, 1, Int.MIN_VALUE, Int.MIN_VALUE, 0x40000000, 0x10000000)
        val lowest = intArrayOf(0, 1, 1, Int.MIN_VALUE, 1, 8)
        for (index in values.indices) {
            val value = values[index]
            check(value.takeHighestOneBit() == highest[index])
            check(value.takeLowestOneBit() == lowest[index])
            for (distance in distances) {
                check(value.rotateLeft(distance) == System.Numerics.BitOperations.RotateLeft(value.toUInt(), distance).toInt())
                check(value.rotateRight(distance) == System.Numerics.BitOperations.RotateRight(value.toUInt(), distance).toInt())
            }
        }
        val longs = longArrayOf(0L, 1L, -1L, Long.MIN_VALUE, Long.MAX_VALUE, 0x123456789abcdef0L)
        val highLongs = longArrayOf(0L, 1L, Long.MIN_VALUE, Long.MIN_VALUE, 0x4000000000000000L, 0x1000000000000000L)
        val lowLongs = longArrayOf(0L, 1L, 1L, Long.MIN_VALUE, 1L, 0x10L)
        for (index in longs.indices) {
            val value = longs[index]
            check(value.takeHighestOneBit() == highLongs[index])
            check(value.takeLowestOneBit() == lowLongs[index])
            for (distance in distances) {
                check(value.rotateLeft(distance) == System.Numerics.BitOperations.RotateLeft(value.toULong(), distance).toLong())
                check(value.rotateRight(distance) == System.Numerics.BitOperations.RotateRight(value.toULong(), distance).toLong())
            }
        }
    }

    @TestAttribute fun logarithmsRejectEveryInvalidBaseAndUseTheUpstreamRatio() {
        for (base in doubleArrayOf(0.0, -0.0, -1.0, 1.0, Double.NEGATIVE_INFINITY, Double.NaN)) {
            for (value in doubleArrayOf(1.0, 2.0, 0.0, Double.POSITIVE_INFINITY, -1.0, Double.NaN)) {
                check(log(value, base).isNaN())
            }
        }
        for (base in doubleArrayOf(0.5, 2.0, 10.0, Double.POSITIVE_INFINITY)) {
            for (value in doubleArrayOf(0.0, 0.5, 1.0, 2.0, Double.MIN_VALUE, Double.MAX_VALUE, Double.POSITIVE_INFINITY)) {
                sameNumericDouble(log(value, base), ln(value) / ln(base))
            }
        }
        for (base in floatArrayOf(0.0f, -0.0f, -1.0f, 1.0f, Float.NEGATIVE_INFINITY, Float.NaN)) {
            for (value in floatArrayOf(1.0f, 2.0f, 0.0f, Float.POSITIVE_INFINITY, -1.0f, Float.NaN)) {
                check(log(value, base).isNaN())
            }
        }
        for (base in floatArrayOf(0.5f, 2.0f, 10.0f, Float.POSITIVE_INFINITY)) {
            for (value in floatArrayOf(0.0f, 0.5f, 1.0f, 2.0f, Float.MIN_VALUE, Float.MAX_VALUE, Float.POSITIVE_INFINITY)) {
                sameNumericFloat(log(value, base), ln(value) / ln(base))
            }
        }
    }

    @TestAttribute fun truncationSignAndUlpKeepZerosSubnormalsAndExtremes() {
        sameNumericDouble(truncate(-0.25), -0.0)
        sameNumericDouble(truncate(0.25), 0.0)
        check(truncate(-2.75) == -2.0 && truncate(2.75) == 2.0)
        sameNumericFloat(truncate(-0.25f), -0.0f)
        sameNumericFloat(truncate(0.25f), 0.0f)
        check(truncate(-2.75f) == -2.0f && truncate(2.75f) == 2.0f)
        for (value in doubleArrayOf(-0.0, 0.0, Double.NEGATIVE_INFINITY, Double.POSITIVE_INFINITY, Double.NaN)) {
            sameNumericDouble(truncate(value), value)
        }
        for (value in floatArrayOf(-0.0f, 0.0f, Float.NEGATIVE_INFINITY, Float.POSITIVE_INFINITY, Float.NaN)) {
            sameNumericFloat(truncate(value), value)
        }
        sameNumericDouble(sign(-0.0), -0.0)
        sameNumericDouble(sign(0.0), 0.0)
        sameNumericFloat(sign(-0.0f), -0.0f)
        sameNumericFloat(sign(0.0f), 0.0f)
        check(sign(-Double.MIN_VALUE) == -1.0 && sign(Double.MAX_VALUE) == 1.0 && sign(Double.NaN).isNaN())
        check(sign(-Float.MIN_VALUE) == -1.0f && sign(Float.MAX_VALUE) == 1.0f && sign(Float.NaN).isNaN())
        sameNumericDouble(0.0.ulp, Double.MIN_VALUE)
        sameNumericDouble((-0.0).ulp, Double.MIN_VALUE)
        sameNumericDouble(Double.MIN_VALUE.ulp, Double.MIN_VALUE)
        sameNumericDouble(1.0.ulp, Double.fromBits(0x3cb0000000000000L))
        sameNumericDouble(2.0.ulp, Double.fromBits(0x3cc0000000000000L))
        sameNumericDouble(Double.MAX_VALUE.ulp, Double.fromBits(0x7ca0000000000000L))
        sameNumericDouble((-Double.MAX_VALUE).ulp, Double.MAX_VALUE.ulp)
        check(Double.NaN.ulp.isNaN())
        check(Double.POSITIVE_INFINITY.ulp == Double.POSITIVE_INFINITY)
        check(Double.NEGATIVE_INFINITY.ulp == Double.POSITIVE_INFINITY)
    }

    @TestAttribute fun extremaAndIntegerPoliciesKeepNaNsSignedZeroAndSaturation() {
        sameNumericDouble(min(0.0, -0.0), -0.0)
        sameNumericDouble(min(-0.0, 0.0), -0.0)
        sameNumericDouble(max(-0.0, 0.0), 0.0)
        sameNumericDouble(max(0.0, -0.0), 0.0)
        sameNumericFloat(min(0.0f, -0.0f), -0.0f)
        sameNumericFloat(max(-0.0f, 0.0f), 0.0f)
        for (a in doubleArrayOf(Double.NEGATIVE_INFINITY, -1.0, -0.0, 0.0, 1.0, Double.POSITIVE_INFINITY, Double.NaN)) {
            for (b in doubleArrayOf(Double.NEGATIVE_INFINITY, -1.0, -0.0, 0.0, 1.0, Double.POSITIVE_INFINITY, Double.NaN)) {
                sameNumericDouble(min(a, b), System.Math.Min(a, b))
                sameNumericDouble(max(a, b), System.Math.Max(a, b))
            }
        }
        for (a in floatArrayOf(Float.NEGATIVE_INFINITY, -1.0f, -0.0f, 0.0f, 1.0f, Float.POSITIVE_INFINITY, Float.NaN)) {
            for (b in floatArrayOf(Float.NEGATIVE_INFINITY, -1.0f, -0.0f, 0.0f, 1.0f, Float.POSITIVE_INFINITY, Float.NaN)) {
                sameNumericFloat(min(a, b), System.MathF.Min(a, b))
                sameNumericFloat(max(a, b), System.MathF.Max(a, b))
            }
        }
        for (a in intArrayOf(Int.MIN_VALUE, -1, 0, 1, Int.MAX_VALUE)) {
            check(a.sign == System.Math.Sign(a))
            for (b in intArrayOf(Int.MIN_VALUE, -1, 0, 1, Int.MAX_VALUE)) {
                check(min(a, b) == System.Math.Min(a, b) && max(a, b) == System.Math.Max(a, b))
            }
        }
        for (a in longArrayOf(Long.MIN_VALUE, -1L, 0L, 1L, Long.MAX_VALUE)) {
            check(a.sign == System.Math.Sign(a))
            for (b in longArrayOf(Long.MIN_VALUE, -1L, 0L, 1L, Long.MAX_VALUE)) {
                check(min(a, b) == System.Math.Min(a, b) && max(a, b) == System.Math.Max(a, b))
            }
        }
        check(8_388_609f.roundToInt() == 8_388_609 && (-8_388_609f).roundToInt() == -8_388_609)
        check(Double.MAX_VALUE.roundToInt() == Int.MAX_VALUE && (-Double.MAX_VALUE).roundToInt() == Int.MIN_VALUE)
        check(Double.fromBits(0x43e0000000000000L).roundToLong() == Long.MAX_VALUE)
        check(Double.MAX_VALUE.roundToLong() == Long.MAX_VALUE && (-Double.MAX_VALUE).roundToLong() == Long.MIN_VALUE)
    }

    @TestAttribute fun savedPrimitiveCallablesUseTheSamePublicContracts() {
        val finite: (Double) -> Boolean = Double::isFinite
        val nan: (Float) -> Boolean = Float::isNaN
        val rotate: (Int, Int) -> Int = Int::rotateLeft
        val rotateLong: (Long, Int) -> Long = Long::rotateRight
        val logarithm: (Double, Double) -> Double = ::log
        check(finite(Double.MIN_VALUE) && !finite(Double.NaN) && nan(Float.NaN))
        check(rotate(0x12345678, 8) == 0x34567812)
        check(rotateLong(Long.MIN_VALUE, 1) == 0x4000000000000000L)
        check(logarithm(1.0, 0.0).isNaN())
        check(logarithm(8.0, 2.0) == 3.0)
    }
}
