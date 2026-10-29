package roundtriptests.frombits

import NUnit.Framework.TestAttribute
import roundtrip.frombits.*

class FromBitsTests {
    @TestAttribute fun ordinaryAndImportedInlineCallsPreserveBits() {
        var calls = 0
        for (bits in intArrayOf(0, Int.MIN_VALUE, 1, 0x7f7fffff, 0x7f800000,
            Int.MIN_VALUE or 0x7f800000, 0x7fc00001, Int.MIN_VALUE or 0x7fc00002)) {
            check(floatFromBits(bits).toRawBits() == bits)
            check(observedFloatFromBits(bits) { calls++; it }.toRawBits() == bits)
        }
        for (bits in longArrayOf(0L, Long.MIN_VALUE, 1L, 0x7fefffffffffffffL,
            0x7ff0000000000000L, Long.MIN_VALUE or 0x7ff0000000000000L,
            0x7ff8000000000001L, Long.MIN_VALUE or 0x7ff8000000000002L)) {
            check(doubleFromBits(bits).toRawBits() == bits)
            check(observedDoubleFromBits(bits) { calls++; it }.toRawBits() == bits)
        }
        check(calls == 16)
    }

    @TestAttribute fun referencesReturnedFromAnotherAssemblyRemainCallable() {
        val f = savedFloatFromBits()
        val d = savedDoubleFromBits()
        check(f(Int.MIN_VALUE).toRawBits() == Int.MIN_VALUE)
        check(f(0x3f800000) == 1f)
        check(f(0x7fc00001).toRawBits() == 0x7fc00001)
        check(d(Long.MIN_VALUE).toRawBits() == Long.MIN_VALUE)
        check(d(0x3ff0000000000000L) == 1.0)
        check(d(0x7ff8000000000001L).toRawBits() == 0x7ff8000000000001L)
    }
}
