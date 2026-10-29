import NUnit.Framework.TestAttribute

class FromBitsTests {
    @TestAttribute fun floatConstructionPreservesRawBits() {
        val patterns = intArrayOf(0, Int.MIN_VALUE, 1, Int.MIN_VALUE or 1,
            0x007fffff, 0x00800000, 0x3f800000, Int.MIN_VALUE or 0x3f800000,
            0x7f7fffff, 0x7f800000, Int.MIN_VALUE or 0x7f800000,
            0x7fc00001, 0x7fffffff, Int.MIN_VALUE or 0x7fc00002)
        for (bits in patterns) check(Float.fromBits(bits).toRawBits() == bits)
        check(Float.fromBits(0x3f800000) == 1f)
        check(Float.fromBits(0x7f800000) == Float.POSITIVE_INFINITY)
        check(Float.fromBits(0x7fc00001).isNaN())
        check(Float.fromBits(Int.MIN_VALUE or 0x7fc00002).toBits() == 0x7fc00000)
    }

    @TestAttribute fun doubleConstructionPreservesRawBits() {
        val patterns = longArrayOf(0L, Long.MIN_VALUE, 1L, Long.MIN_VALUE or 1L,
            0x000fffffffffffffL, 0x0010000000000000L, 0x3ff0000000000000L,
            Long.MIN_VALUE or 0x3ff0000000000000L, 0x7fefffffffffffffL,
            0x7ff0000000000000L, Long.MIN_VALUE or 0x7ff0000000000000L,
            0x7ff8000000000001L, 0x7fffffffffffffffL, Long.MIN_VALUE or 0x7ff8000000000002L)
        for (bits in patterns) check(Double.fromBits(bits).toRawBits() == bits)
        check(Double.fromBits(0x3ff0000000000000L) == 1.0)
        check(Double.fromBits(0x7ff0000000000000L) == Double.POSITIVE_INFINITY)
        check(Double.fromBits(0x7ff8000000000001L).isNaN())
        check(Double.fromBits(Long.MIN_VALUE or 0x7ff8000000000002L).toBits() == 0x7ff8000000000000L)
    }

    @TestAttribute fun companionReceiversArgumentsAndBoundReferencesAreEvaluatedOnce() {
        var receivers = 0
        var arguments = 0
        fun floatReceiver(): Float.Companion { receivers++; return Float }
        fun doubleReceiver(): Double.Companion { receivers++; return Double }
        fun floatBits(): Int { arguments++; return Int.MIN_VALUE }
        fun doubleBits(): Long { arguments++; return Long.MIN_VALUE }
        check(floatReceiver().fromBits(floatBits()).toRawBits() == Int.MIN_VALUE)
        check(doubleReceiver().fromBits(doubleBits()).toRawBits() == Long.MIN_VALUE)
        check(receivers == 2 && arguments == 2)
        val f: (Int) -> Float = floatReceiver()::fromBits
        val d: (Long) -> Double = doubleReceiver()::fromBits
        check(receivers == 4)
        check(f(0x7fc00001).toRawBits() == 0x7fc00001)
        check(f(Int.MIN_VALUE).toRawBits() == Int.MIN_VALUE)
        check(d(0x7ff8000000000001L).toRawBits() == 0x7ff8000000000001L)
        check(d(Long.MIN_VALUE).toRawBits() == Long.MIN_VALUE)
        check(receivers == 4 && arguments == 2)
    }
}
