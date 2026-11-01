@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import PortableArrayContentInterop.Arrays as NativeContentArrays
import UnsignedArrayMemberInterop.NativeArrays as NativeUnsignedArrays

class NativeArrayContentInteropTests {
    @TestAttribute
    fun nativeArrayReturnsKeepContentOperationsAndAllElementKinds() {
        val text = NativeContentArrays.Text()
        check(text.contentEquals(arrayOf<String?>("first", null)))
        check(text.contentToString() == "[first, null]" && text.elementAt(1) == null)
        val bytes = NativeContentArrays.Bytes()
        check(bytes.contentEquals(byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE)))
        check(bytes.elementAt(0) == Byte.MIN_VALUE && bytes.contentToString() == "[${Byte.MIN_VALUE}, 0, ${Byte.MAX_VALUE}]")
        val shorts = NativeContentArrays.Shorts()
        check(shorts.contentEquals(shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE)))
        check(shorts.elementAt(2) == Short.MAX_VALUE)
        val ints = NativeContentArrays.Ints()
        check(ints.contentEquals(intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE)))
        check(ints.elementAt(0) == Int.MIN_VALUE)
        val hash: () -> Int = ints::contentHashCode
        NativeContentArrays.Change(ints)
        check(hash() == (31 * (31 * (31 + 42)) + Int.MAX_VALUE))
        val longs = NativeContentArrays.Longs()
        check(longs.contentEquals(longArrayOf(Long.MIN_VALUE, 0, Long.MAX_VALUE)))
        check(longs.elementAt(2) == Long.MAX_VALUE)
        val floats = NativeContentArrays.Floats()
        check(floats.contentEquals(floatArrayOf(Float.NaN, -0.0f, 0.0f)))
        check(floats.elementAt(1).toBits() == (-0.0f).toBits())
        val doubles = NativeContentArrays.Doubles()
        check(doubles.contentEquals(doubleArrayOf(Double.NaN, -0.0, 0.0)))
        check(doubles.elementAt(1).toBits() == (-0.0).toBits())
        val booleans = NativeContentArrays.Booleans()
        check(booleans.contentEquals(booleanArrayOf(false, true)) && booleans.elementAt(1))
        val chars = NativeContentArrays.Chars()
        check(chars.contentEquals(charArrayOf('\u0000', 'x')) && chars.elementAt(1) == 'x')
        check(NativeUnsignedArrays.Bytes().elementAt(2) == UByte.MAX_VALUE)
        check(NativeUnsignedArrays.Shorts().elementAt(2) == UShort.MAX_VALUE)
        check(NativeUnsignedArrays.Ints().elementAt(2) == UInt.MAX_VALUE)
        check(NativeUnsignedArrays.Longs().elementAt(2) == ULong.MAX_VALUE)
    }
}
