import NUnit.Framework.TestAttribute
import kotlin.math.*

class FloatingNeighborTests {
    @TestAttribute fun doubleNeighborsAndSpacing() {
        for (zero in doubleArrayOf(0.0, -0.0)) {
            check(zero.nextUp().toRawBits() == 1L)
            check(zero.nextDown().toRawBits() == Long.MIN_VALUE + 1L)
            check(zero.ulp.toRawBits() == 1L)
        }
        check(0.0.nextTowards(-0.0).toRawBits() == Long.MIN_VALUE)
        check((-0.0).nextTowards(0.0).toRawBits() == 0L)
        check(Double.MIN_VALUE.nextDown().toRawBits() == 0L)
        check((-Double.MIN_VALUE).nextUp().toRawBits() == Long.MIN_VALUE)
        val normal = 1L shl 52
        check(Double.fromBits(normal - 1).nextTowards(1.0).toRawBits() == normal)
        check(Double.fromBits(normal).nextTowards(0.0).toRawBits() == normal - 1)
        check(Double.fromBits(normal).ulp.toRawBits() == 1L)
        check((-1.0).ulp.toRawBits() == (971L shl 52))
        check(Double.MAX_VALUE.ulp.toRawBits() == (1994L shl 52))
        check(Double.MAX_VALUE.nextUp() == Double.POSITIVE_INFINITY)
        check((-Double.MAX_VALUE).nextDown() == Double.NEGATIVE_INFINITY)
        check(Double.POSITIVE_INFINITY.nextDown() == Double.MAX_VALUE)
        check(Double.NEGATIVE_INFINITY.nextUp() == -Double.MAX_VALUE)
        check(Double.NEGATIVE_INFINITY.ulp == Double.POSITIVE_INFINITY)
        check(Double.NEGATIVE_INFINITY.nextDown() == Double.NEGATIVE_INFINITY)
        check(Double.POSITIVE_INFINITY.nextUp() == Double.POSITIVE_INFINITY)
        for (bits in longArrayOf(0x7ff8000000000042L, Long.MIN_VALUE or 0x7ff8000000000017L)) {
            val nan = Double.fromBits(bits)
            check(nan.nextUp().toRawBits() == bits && nan.nextDown().toRawBits() == bits)
            check(nan.ulp.isNaN() && nan.nextTowards(1.0).isNaN() && 1.0.nextTowards(nan).isNaN())
            check(nan.withSign(-0.0).toRawBits() == (bits or Long.MIN_VALUE))
            check(nan.withSign(1).toRawBits() == (bits and Long.MAX_VALUE))
            check(1.0.withSign(nan).toRawBits() == System.Math.CopySign(1.0, nan).toRawBits())
        }
        // Copying only the sign bit must not quiet a signaling NaN.
        val signaling = 0x7ff0000000000042L
        check(Double.fromBits(signaling).withSign(-1).toRawBits() == (signaling or Long.MIN_VALUE))
    }

    @TestAttribute fun floatNeighborsAndSpacing() {
        for (zero in floatArrayOf(0f, -0f)) {
            check(zero.nextUp().toRawBits() == 1)
            check(zero.nextDown().toRawBits() == Int.MIN_VALUE + 1)
            check(zero.ulp.toRawBits() == 1)
        }
        check(0f.nextTowards(-0f).toRawBits() == Int.MIN_VALUE)
        check((-0f).nextTowards(0f).toRawBits() == 0)
        check(Float.MIN_VALUE.nextDown().toRawBits() == 0)
        check((-Float.MIN_VALUE).nextUp().toRawBits() == Int.MIN_VALUE)
        val normal = 1 shl 23
        check(Float.fromBits(normal - 1).nextTowards(1f).toRawBits() == normal)
        check(Float.fromBits(normal).nextTowards(0f).toRawBits() == normal - 1)
        check(Float.fromBits(normal).ulp.toRawBits() == 1)
        check((-1f).ulp.toRawBits() == (104 shl 23))
        check(Float.MAX_VALUE.ulp.toRawBits() == (231 shl 23))
        check(Float.MAX_VALUE.nextUp() == Float.POSITIVE_INFINITY)
        check((-Float.MAX_VALUE).nextDown() == Float.NEGATIVE_INFINITY)
        check(Float.POSITIVE_INFINITY.nextDown() == Float.MAX_VALUE)
        check(Float.NEGATIVE_INFINITY.nextUp() == -Float.MAX_VALUE)
        check(Float.NEGATIVE_INFINITY.ulp == Float.POSITIVE_INFINITY)
        check(Float.NEGATIVE_INFINITY.nextDown() == Float.NEGATIVE_INFINITY)
        check(Float.POSITIVE_INFINITY.nextUp() == Float.POSITIVE_INFINITY)
        for (bits in intArrayOf(0x7fc00042, Int.MIN_VALUE or 0x7fc00017)) {
            val nan = Float.fromBits(bits)
            check(nan.nextUp().toRawBits() == bits && nan.nextDown().toRawBits() == bits)
            check(nan.ulp.isNaN() && nan.nextTowards(1f).isNaN() && 1f.nextTowards(nan).isNaN())
            check(nan.withSign(-0f).toRawBits() == (bits or Int.MIN_VALUE))
            check(nan.withSign(1).toRawBits() == (bits and Int.MAX_VALUE))
            check(1f.withSign(nan).toRawBits() == System.MathF.CopySign(1f, nan).toRawBits())
        }
        val signaling = 0x7f800042
        check(Float.fromBits(signaling).withSign(-1).toRawBits() == (signaling or Int.MIN_VALUE))
    }
}
