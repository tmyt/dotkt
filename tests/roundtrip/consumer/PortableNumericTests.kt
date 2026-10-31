package roundtriptests.portablenumeric

import NUnit.Framework.TestAttribute
import roundtrip.portablenumeric.*

class PortableNumericTests {
    @TestAttribute fun importedIeeePoliciesAndSavedClassifierKeepSourceContracts() {
        check(logarithm(1.0, 0.0).isNaN())
        check(logarithmFloat(1.0f, -0.0f).isNaN())
        check(logarithm(2.0, Double.POSITIVE_INFINITY).toRawBits() == 0L)
        check(logarithmFloat(2.0f, Float.POSITIVE_INFINITY).toRawBits() == 0)
        check(logarithm(8.0, 2.0) == 3.0)
        check(unitInLastPlace(-Double.MAX_VALUE) == Double.fromBits(0x7ca0000000000000L))
        val finite = savedFinite()
        check(finite(Double.MIN_VALUE) && !finite(Double.POSITIVE_INFINITY) && !finite(Double.NaN))
    }

    @TestAttribute fun importedBitPoliciesAndInlineCallbacksKeepExactValues() {
        check(rotateWord(0x12345678, 8) == 0x34567812)
        for (distance in intArrayOf(Int.MIN_VALUE, -65, -1, 0, 1, 64, 65, Int.MAX_VALUE)) {
            check(rotateWide(Long.MIN_VALUE, distance) == System.Numerics.BitOperations.RotateRight(Long.MIN_VALUE.toULong(), distance).toLong())
        }
        check(savedWideRotation()(Long.MIN_VALUE, 1) == 0x4000000000000000L)
        var calls = 0
        val result = observeLog(8.0) {
            calls++
            it * 2.0
        }
        check(calls == 1 && result == 6.0)
    }
}
