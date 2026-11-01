@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

private open class PortableArrayContentValue(private val label: String) {
    var hashes = 0
    override fun hashCode(): Int { hashes++; return 37 }
    override fun equals(other: Any?): Boolean = other is PortableArrayContentValue && label == other.label
    override fun toString(): String = label
}

private class PortableArrayContentNarrow : PortableArrayContentValue("narrow")

@Suppress("UNCHECKED_CAST")
private fun <T> portableUncheckedNullContent(): Boolean {
    val first = arrayOfNulls<Any>(1) as Array<T>
    val second = arrayOfNulls<Any>(1) as Array<T>
    return first.contentHashCode() == 31 && first.contentToString() == "[null]" && first.contentEquals(second)
}

@Suppress("UNCHECKED_CAST")
private fun <T : PortableArrayContentValue> portableUncheckedBoundedContent(): Boolean {
    val value = PortableArrayContentValue("erased")
    val first = arrayOf(value) as Array<T>
    val second = arrayOf(value) as Array<T>
    return first.contentHashCode() == 68 && first.contentToString() == "[erased]" && first.contentEquals(second)
}

private fun <T> portableGenericArrayHash(values: Array<out T>?): Int = values.contentHashCode()
private fun <T> portableGenericArrayText(values: Array<out T>?): String = values.contentToString()
private fun <T> portableGenericArrayEquals(first: Array<out T>?, second: Array<out T>?): Boolean = first.contentEquals(second)
private fun portableArrayFold(size: Int, elementHash: (Int) -> Int): Int {
    var result = 1
    for (index in 0 until size) result = 31 * result + elementHash(index)
    return result
}

private fun portableElementBounds(size: Int, get: (Int) -> Any?) {
    for (index in listOf(-1, size)) {
        var caught = false
        try { get(index) } catch (_: IndexOutOfBoundsException) { caught = true }
        check(caught)
    }
}

class PortableArrayContentTests {
    @TestAttribute
    fun genericContentKeepsNullsEqualityHashCallsAndAuthoredRendering() {
        check(portableGenericArrayHash(arrayOf(1, 2)) == 994)
        check(portableUncheckedNullContent<Int>() && portableUncheckedNullContent<String>())
        check(portableUncheckedBoundedContent<PortableArrayContentNarrow>())
        check(portableGenericArrayEquals<String>(null, null))
        check(!portableGenericArrayEquals(null, emptyArray<String>()))
        check(!portableGenericArrayEquals(emptyArray<String>(), null))
        check(portableGenericArrayHash<String>(null) == 0 && portableGenericArrayText<String>(null) == "null")
        check(portableGenericArrayHash(emptyArray<String>()) == 1 && portableGenericArrayText(emptyArray<String>()) == "[]")
        check(portableGenericArrayEquals(arrayOf<Any?>(1, null, "text"), arrayOf<Any?>(1, null, "text")))
        check(!portableGenericArrayEquals(arrayOf<Any?>(1, null), arrayOf<Any?>(1, "other")))
        check(!portableGenericArrayEquals(arrayOf(1), arrayOf(1, 2)))
        check(portableGenericArrayEquals(arrayOf<Int?>(null, 7), arrayOf<Int?>(null, 7)))
        check(portableGenericArrayText(arrayOf<Int?>(null, 7)) == "[null, 7]")
        check(portableGenericArrayEquals(arrayOf(Double.NaN), arrayOf(Double.NaN)))
        check(!portableGenericArrayEquals(arrayOf(-0.0), arrayOf(0.0)))
        val value = PortableArrayContentValue("authored")
        val values = arrayOf(value, null, value)
        check(portableGenericArrayEquals(values, arrayOf(PortableArrayContentValue("authored"), null, value)))
        check(portableGenericArrayHash(values) == 65385 && value.hashes == 2)
        check(portableGenericArrayText(values) == "[authored, null, authored]")
        val reference: () -> String = values::contentToString
        values[0] = null
        check(reference() == "[null, null, authored]")
    }

    @TestAttribute
    fun everyPrimitiveContentOverloadKeepsItsValueDomainAndNullContract() {
        val bytes = byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE)
        check(bytes.contentEquals(bytes.copyOf()) && !bytes.contentEquals(byteArrayOf(0)))
        check(bytes.contentHashCode() == portableArrayFold(bytes.size) { bytes[it].hashCode() })
        check(bytes.contentToString() == "[${Byte.MIN_VALUE}, 0, ${Byte.MAX_VALUE}]")
        check((null as ByteArray?).contentEquals(null) && !(null as ByteArray?).contentEquals(bytes))
        check((null as ByteArray?).contentHashCode() == 0 && (null as ByteArray?).contentToString() == "null")
        val shorts = shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE)
        check(shorts.contentEquals(shorts.copyOf()) && !shorts.contentEquals(shortArrayOf(0)))
        check(shorts.contentHashCode() == portableArrayFold(shorts.size) { shorts[it].hashCode() })
        check(shorts.contentToString() == "[${Short.MIN_VALUE}, 0, ${Short.MAX_VALUE}]")
        check((null as ShortArray?).contentEquals(null) && !(null as ShortArray?).contentEquals(shorts))
        check((null as ShortArray?).contentHashCode() == 0 && (null as ShortArray?).contentToString() == "null")
        val ints = intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE)
        check(ints.contentEquals(ints.copyOf()) && !ints.contentEquals(intArrayOf(0)))
        check(ints.contentHashCode() == portableArrayFold(ints.size) { ints[it].hashCode() })
        check(ints.contentToString() == "[${Int.MIN_VALUE}, 0, ${Int.MAX_VALUE}]")
        check((null as IntArray?).contentEquals(null) && !(null as IntArray?).contentEquals(ints))
        check((null as IntArray?).contentHashCode() == 0 && (null as IntArray?).contentToString() == "null")
        val longs = longArrayOf(Long.MIN_VALUE, 0, Long.MAX_VALUE)
        check(longs.contentEquals(longs.copyOf()) && !longs.contentEquals(longArrayOf(0)))
        check(longs.contentHashCode() == portableArrayFold(longs.size) { longs[it].hashCode() })
        check(longs.contentToString() == "[${Long.MIN_VALUE}, 0, ${Long.MAX_VALUE}]")
        check((null as LongArray?).contentEquals(null) && !(null as LongArray?).contentEquals(longs))
        check((null as LongArray?).contentHashCode() == 0 && (null as LongArray?).contentToString() == "null")
        val floats = floatArrayOf(Float.NaN, -0.0f, 0.0f, Float.POSITIVE_INFINITY)
        check(floats.contentEquals(floats.copyOf()))
        check(floatArrayOf(Float.NaN).contentEquals(floatArrayOf(Float.fromBits(0x7fc00001))))
        check(!floatArrayOf(-0.0f).contentEquals(floatArrayOf(0.0f)))
        check(floats.contentHashCode() == portableArrayFold(floats.size) { floats[it].hashCode() })
        check(floats.contentToString() == "[${floats[0]}, ${floats[1]}, ${floats[2]}, ${floats[3]}]")
        check((null as FloatArray?).contentEquals(null) && !(null as FloatArray?).contentEquals(floats))
        check((null as FloatArray?).contentHashCode() == 0 && (null as FloatArray?).contentToString() == "null")
        val doubles = doubleArrayOf(Double.NaN, -0.0, 0.0, Double.POSITIVE_INFINITY)
        check(doubles.contentEquals(doubles.copyOf()))
        check(doubleArrayOf(Double.NaN).contentEquals(doubleArrayOf(Double.fromBits(0x7ff8000000000001L))))
        check(!doubleArrayOf(-0.0).contentEquals(doubleArrayOf(0.0)))
        check(doubles.contentHashCode() == portableArrayFold(doubles.size) { doubles[it].hashCode() })
        check(doubles.contentToString() == "[${doubles[0]}, ${doubles[1]}, ${doubles[2]}, ${doubles[3]}]")
        check((null as DoubleArray?).contentEquals(null) && !(null as DoubleArray?).contentEquals(doubles))
        check((null as DoubleArray?).contentHashCode() == 0 && (null as DoubleArray?).contentToString() == "null")
        val booleans = booleanArrayOf(false, true)
        check(booleans.contentEquals(booleans.copyOf()) && !booleans.contentEquals(booleanArrayOf(true)))
        check(booleans.contentHashCode() == portableArrayFold(booleans.size) { booleans[it].hashCode() })
        check(booleans.contentToString() == "[${false}, ${true}]")
        check((null as BooleanArray?).contentEquals(null) && !(null as BooleanArray?).contentEquals(booleans))
        check((null as BooleanArray?).contentHashCode() == 0 && (null as BooleanArray?).contentToString() == "null")
        val chars = charArrayOf('\u0000', 'x')
        check(chars.contentEquals(chars.copyOf()) && !chars.contentEquals(charArrayOf('x')))
        check(chars.contentHashCode() == portableArrayFold(chars.size) { chars[it].hashCode() })
        check(chars.contentToString() == "[\u0000, x]")
        check((null as CharArray?).contentEquals(null) && !(null as CharArray?).contentEquals(chars))
        check((null as CharArray?).contentHashCode() == 0 && (null as CharArray?).contentToString() == "null")
        check(byteArrayOf().contentHashCode() == 1 && byteArrayOf().contentToString() == "[]")
    }

    @TestAttribute
    fun everyElementAtAdapterPreservesBoundsAndEvaluationOrder() {
        val getters = listOf<(Int) -> Any?>(
            { arrayOf("a", "b").elementAt(it) }, { byteArrayOf(-1, 7).elementAt(it) },
            { shortArrayOf(-1, 7).elementAt(it) }, { intArrayOf(-1, 7).elementAt(it) },
            { longArrayOf(-1, 7).elementAt(it) }, { floatArrayOf(-1f, 7f).elementAt(it) },
            { doubleArrayOf(-1.0, 7.0).elementAt(it) }, { booleanArrayOf(false, true).elementAt(it) },
            { charArrayOf('a', 'b').elementAt(it) }, { ubyteArrayOf(0u, UByte.MAX_VALUE).elementAt(it) },
            { ushortArrayOf(0u, UShort.MAX_VALUE).elementAt(it) }, { uintArrayOf(0u, UInt.MAX_VALUE).elementAt(it) },
            { ulongArrayOf(0u, ULong.MAX_VALUE).elementAt(it) },
        )
        val expected = listOf<Any?>("b", 7.toByte(), 7.toShort(), 7, 7L, 7f, 7.0, true, 'b',
            UByte.MAX_VALUE, UShort.MAX_VALUE, UInt.MAX_VALUE, ULong.MAX_VALUE)
        for (index in getters.indices) {
            check(getters[index](1) == expected[index])
            portableElementBounds(2, getters[index])
        }
        portableElementBounds(0) { emptyArray<String>().elementAt(it) }
        var order = ""
        fun receiver(): IntArray { order += "r"; return intArrayOf(3, 7) }
        fun index(): Int { order += "i"; return 1 }
        check(receiver().elementAt(index()) == 7 && order == "ri")
    }
}
