@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import UnsignedArrayMemberInterop.NativeArrays
import kotlin.clr.byref

class UnsignedArrayMemberInteropTests {
    @TestAttribute
    fun nativeArgumentsMutationAndRefOutKeepMemberQueriesLive() {
        var bytes = NativeArrays.Bytes()
        var shorts = NativeArrays.Shorts()
        var ints = NativeArrays.Ints()
        var longs = NativeArrays.Longs()
        check(bytes.containsAll(listOf<UByte>(0u, 128u, UByte.MAX_VALUE)))
        check(shorts.containsAll(listOf<UShort>(0u, 32768u, UShort.MAX_VALUE)))
        check(ints.containsAll(listOf(0u, 2147483648u, UInt.MAX_VALUE)))
        check(longs.containsAll(listOf(0uL, 9223372036854775808uL, ULong.MAX_VALUE)))
        val signed = ints.asIntArray()
        NativeArrays.Touch(bytes, shorts, ints, longs)
        check(UByte.MAX_VALUE in bytes && UShort.MAX_VALUE in shorts)
        check(UInt.MAX_VALUE in ints && ULong.MAX_VALUE in longs && signed[0] == -1)
        NativeArrays.Replace(byref(bytes), byref(shorts), byref(ints), byref(longs))
        check(bytes.size == 1 && bytes.containsAll(listOf<UByte>(7u)))
        check(shorts.size == 1 && shorts.containsAll(listOf<UShort>(7u)))
        check(ints.size == 1 && ints.containsAll(listOf(7u)))
        check(longs.size == 1 && longs.containsAll(listOf(7uL)))
        check(signed[0] == -1)
    }
}
