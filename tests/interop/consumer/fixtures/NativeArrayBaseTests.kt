@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import NativeArrayBaseInterop.Api

private fun <T : System.Array> clearNativeArray(value: T) {
    System.Array.Clear(value, 0, 1)
}

private fun <T> clearGenericArray(value: Array<T>) {
    System.Array.Clear(value, 0, value.size)
}

class NativeArrayBaseTests {
    @TestAttribute
    fun primitiveArraysReachTheExactNativeBaseWithoutCopying() {
        val chars = charArrayOf('a')
        check(chars.Length == 1 && chars.Rank == 1)
        val clone = chars.Clone() as CharArray
        check(clone !== chars && clone[0] == 'a')
        clone[0] = 'z'
        check(chars[0] == 'a')
        System.Array.Clear(chars, 0, 1)
        check(chars[0] == '\u0000')
        val native: System.Array = chars
        check(native === chars && Api.Echo(chars) === chars)
        check(Api.Select(chars) == "array" && Api.Select(42) == "object")
        chars[0] = 'b'
        CharArray.Clear(chars, 0, 1)
        check(chars[0] == '\u0000')
        chars[0] = 'c'
        clearNativeArray(chars)
        check(chars[0] == '\u0000')
        val bytes = byteArrayOf(7)
        val shorts = shortArrayOf(7)
        val ints = intArrayOf(7)
        val longs = longArrayOf(7L)
        val floats = floatArrayOf(7.0f)
        val doubles = doubleArrayOf(7.0)
        val booleans = booleanArrayOf(true)
        Api.Clear(bytes, bytes, "System.SByte")
        Api.Clear(shorts, shorts, "System.Int16")
        Api.Clear(ints, ints, "System.Int32")
        Api.Clear(longs, longs, "System.Int64")
        Api.Clear(floats, floats, "System.Single")
        Api.Clear(doubles, doubles, "System.Double")
        Api.Clear(booleans, booleans, "System.Boolean")
        check(bytes[0] == 0.toByte() && shorts[0] == 0.toShort() && ints[0] == 0 && longs[0] == 0L)
        check(floats[0] == 0.0f && doubles[0] == 0.0 && !booleans[0])
    }

    @TestAttribute
    fun unsignedAndGenericArraysKeepTheirNativeIdentityAndComponent() {
        val bytes = ubyteArrayOf(255u)
        val shorts = ushortArrayOf(65535u)
        val ints = uintArrayOf(UInt.MAX_VALUE)
        val longs = ulongArrayOf(ULong.MAX_VALUE)
        check(ints.Length == 1 && ints.GetValue(0).toString() == "4294967295")
        Api.Clear(bytes, bytes, "System.Byte")
        Api.Clear(shorts, shorts, "System.UInt16")
        Api.Clear(ints, ints, "System.UInt32")
        Api.Clear(longs, longs, "System.UInt64")
        check(bytes[0] == 0.toUByte() && shorts[0] == 0.toUShort() && ints[0] == 0u && longs[0] == 0uL)
        val names = arrayOf<String?>("name")
        Api.Clear(names, names, "System.String")
        check(names[0] == null)
        val numbers = arrayOf(7)
        Api.Clear(numbers, numbers, "System.Int32")
        check(numbers[0] == 0)
        val open = arrayOf<String?>("open")
        clearGenericArray(open)
        check(open[0] == null && Api.Echo(open) === open)
        val nested = arrayOf(intArrayOf(7))
        check(Api.Select(nested) == "array" && Api.Echo(nested) === nested)
    }
}
