package roundtriptests.primitivesorting

import NUnit.Framework.TestAttribute
import roundtrip.primitivesorting.*

class PrimitiveArraySortingTests {
    @TestAttribute fun primitiveSortsKeepNativeStorageAndTotalOrderAcrossDlls() {
        val d = doubleArrayOf(99.0, Double.NaN, 1.0, 0.0, -0.0, 88.0)
        val native = d as System.Array
        sortDoubleRange(d, 1, 5)
        check(d[0] == 99.0 && d[5] == 88.0 && d[4].isNaN())
        check(d[3] == 1.0 && System.BitConverter.DoubleToInt64Bits(d[1]) == Long.MIN_VALUE)
        check(System.BitConverter.DoubleToInt64Bits(d[2]) == 0L && (d as Any) === native)
        val f = floatArrayOf(99f, Float.NaN, 1f, 0f, -0f, 88f)
        sortFloatRange(f, 1, 5)
        check(f[0] == 99f && f[5] == 88f && f[4].isNaN())
        check(f[3] == 1f && System.BitConverter.SingleToInt32Bits(f[1]) == Int.MIN_VALUE)
        check(System.BitConverter.SingleToInt32Bits(f[2]) == 0)
    }
}
