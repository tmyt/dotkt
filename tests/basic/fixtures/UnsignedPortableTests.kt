import NUnit.Framework.TestAttribute
import System.Reflection.BindingFlags
import System.Type

// Native aliases can bypass Kotlin helper bodies. Exercise the real current stdlib methods by exact CLR signature too.
private fun unsignedPortableHelper(name: String, vararg args: Any?): Any? {
    val owner = Type.GetType("kotlin.UnsignedKt, DotKt.Stdlib")!!
    val types = Array<Type>(args.size) { (args[it] as System.Object).GetType() }
    val flags = BindingFlags.Public or BindingFlags.NonPublic or BindingFlags.Static
    return owner.GetMethod(name, flags, null, types, null)!!.Invoke(null, Array<Any?>(args.size) { args[it] })
}

private fun unsignedPortableDigits(value: ULong, radix: Int): String {
    var remaining = value
    val base = radix.toULong()
    var result = ""
    do {
        val digit = (remaining % base).toInt()
        result = "0123456789abcdefghijklmnopqrstuvwxyz"[digit] + result
        remaining /= base
    } while (remaining != 0uL)
    return result
}

class UnsignedPortableTests {
    @TestAttribute fun allRadicesUsePortableBodiesForBothWidths() {
        for (radix in 2..36) {
            for (raw in intArrayOf(0, 1, Int.MAX_VALUE, Int.MIN_VALUE, -1)) {
                val value = raw.toUInt()
                val expected = unsignedPortableDigits((raw.toLong() and 0xffffffffL).toULong(), radix)
                check(value.toString(radix) == expected)
                check(unsignedPortableHelper("uintToString", raw, radix) as String == expected)
                check(expected.toUInt(radix) == value)
            }
            for (raw in longArrayOf(0L, 1L, Long.MAX_VALUE, Long.MIN_VALUE, -1L)) {
                val value = raw.toULong()
                val expected = unsignedPortableDigits(value, radix)
                check(value.toString(radix) == expected)
                check(unsignedPortableHelper("ulongToString", raw, radix) as String == expected)
                check(expected.toULong(radix) == value)
            }
        }
        check(unsignedPortableHelper("uintToString", -1) as String == "4294967295")
        check(unsignedPortableHelper("ulongToString", -1L) as String == "18446744073709551615")
        check(UInt.MAX_VALUE.toString(36) == "1z141z3")
        check(ULong.MAX_VALUE.toString(36) == "3w5e11264sgsf")
    }

    @TestAttribute fun uintMasksOrderingAndFloatingBoundariesMatchNativeValues() {
        val values = intArrayOf(0, 1, Int.MAX_VALUE, Int.MIN_VALUE, -1, 16777217, 16777219)
        for (raw in values) {
            val positive = raw.toLong() and 0xffffffffL
            check(unsignedPortableHelper("uintToLong", raw) as Long == positive)
            check(unsignedPortableHelper("uintToULong", raw) as ULong == positive.toULong())
            check((unsignedPortableHelper("uintToDouble", raw) as Double).toRawBits() == positive.toDouble().toRawBits())
            check((unsignedPortableHelper("uintToFloat", raw) as Float).toRawBits() == positive.toFloat().toRawBits())
            check(raw.toUInt().toLong() == positive)
            check(raw.toUInt().toULong() == positive.toULong())
            for (other in values) {
                val expected = positive.compareTo(other.toLong() and 0xffffffffL).compareTo(0)
                check((unsignedPortableHelper("uintCompare", raw, other) as Int).compareTo(0) == expected)
            }
        }
    }

    @TestAttribute fun ulongOrderingAndFloatingBoundariesPreserveHighBits() {
        val values = longArrayOf(0L, 1L, Long.MAX_VALUE, Long.MIN_VALUE, -1L, 9007199254740993L)
        for (raw in values) {
            val expected = System.Convert.ToDouble(raw.toULong())
            check((unsignedPortableHelper("ulongToDouble", raw) as Double).toRawBits() == expected.toRawBits())
            check((unsignedPortableHelper("ulongToFloat", raw) as Float).toRawBits() == expected.toFloat().toRawBits())
            check(raw.toULong().toDouble().toRawBits() == expected.toRawBits())
            for (other in values) {
                val order = (raw xor Long.MIN_VALUE).compareTo(other xor Long.MIN_VALUE).compareTo(0)
                check((unsignedPortableHelper("ulongCompare", raw, other) as Int).compareTo(0) == order)
            }
        }
    }

    @TestAttribute fun floatingToUnsignedHelpersAndPublicApisSaturate() {
        val values = doubleArrayOf(Double.NaN, Double.NEGATIVE_INFINITY, -1.0, -0.0, 0.0, 0.9, 1.9,
            2147483647.0, 2147483648.0, 4294967294.75, 4294967295.0, 4294967296.0, Double.POSITIVE_INFINITY)
        val expected = listOf(0u, 0u, 0u, 0u, 0u, 0u, 1u, 2147483647u, 2147483648u,
            4294967294u, UInt.MAX_VALUE, UInt.MAX_VALUE, UInt.MAX_VALUE)
        for (index in values.indices) {
            check(unsignedPortableHelper("doubleToUInt", values[index]) as UInt == expected[index])
            check(values[index].toUInt() == expected[index])
        }
        val wide = doubleArrayOf(Double.NaN, Double.NEGATIVE_INFINITY, -1.0, -0.0, 0.9, 1.9,
            9223372036854773760.0, 9223372036854775808.0, 9223372036854777856.0,
            18446744073709549568.0, 18446744073709551616.0, Double.POSITIVE_INFINITY)
        val wideExpected = listOf(0uL, 0uL, 0uL, 0uL, 0uL, 1uL, 9223372036854773760uL,
            9223372036854775808uL, 9223372036854777856uL, 18446744073709549568uL, ULong.MAX_VALUE, ULong.MAX_VALUE)
        for (index in wide.indices) {
            check(unsignedPortableHelper("doubleToULong", wide[index]) as ULong == wideExpected[index])
            check(wide[index].toULong() == wideExpected[index])
        }
        for (value in floatArrayOf(Float.NaN, Float.NEGATIVE_INFINITY, -1f, 0f, 0.9f, 1.9f,
            2147483648f, 4294967296f, Float.POSITIVE_INFINITY)) {
            check(unsignedPortableHelper("floatToUInt", value) as UInt == value.toDouble().toUInt())
            check(unsignedPortableHelper("floatToULong", value) as ULong == value.toDouble().toULong())
            check(value.toUInt() == value.toDouble().toUInt())
            check(value.toULong() == value.toDouble().toULong())
        }
    }

    @TestAttribute fun publicRadixChecksSignedControlsAndNativeInteropRemainIntact() {
        for (radix in intArrayOf(Int.MIN_VALUE, 0, 1, 37, Int.MAX_VALUE)) {
            var rejected = false
            try { UInt.MAX_VALUE.toString(radix) } catch (_: IllegalArgumentException) { rejected = true }
            check(rejected)
            rejected = false
            try { ULong.MAX_VALUE.toString(radix) } catch (_: IllegalArgumentException) { rejected = true }
            check(rejected)
        }
        check((-35).toString(36) == "-z" && Long.MIN_VALUE.toString(16) == "-8000000000000000")
        check(255.toUByte().toString(16) == "ff" && 65535.toUShort().toString(16) == "ffff")
        check(System.Math.Max(UInt.MAX_VALUE, 1u) == UInt.MAX_VALUE)
        check(System.Math.Max(ULong.MAX_VALUE, 1uL) == ULong.MAX_VALUE)
        check(System.Convert.ToString(UInt.MAX_VALUE) == "4294967295")
        check(System.Convert.ToString(ULong.MAX_VALUE) == "18446744073709551615")
    }
}
