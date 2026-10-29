import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private fun assertPrimitiveRange(before: List<Int>, after: List<Int>, from: Int, to: Int) {
    val expected = before.toMutableList()
    val middle = before.subList(from, to).sorted()
    for (i in middle.indices) expected[from + i] = middle[i]
    check(after == expected)
}

private fun assertNativePrimitiveValues(native: System.Array, values: List<Any?>) {
    check(native.Length == values.size)
    for (i in values.indices) check(native.GetValue(i) == values[i])
}

private fun assertInvalidPrimitiveSort(sort: (Int, Int) -> Unit, snapshot: () -> String) {
    val before = snapshot()
    val froms = intArrayOf(-1, 0, 2, 4, -1, Int.MIN_VALUE, 0)
    val tos = intArrayOf(2, 4, 1, 4, -2, Int.MAX_VALUE, -1)
    for (i in froms.indices) {
        var failure = "none"
        try { sort(froms[i], tos[i]) }
        catch (_: IndexOutOfBoundsException) { failure = "bounds" }
        catch (_: IllegalArgumentException) { failure = "inverted" }
        assertEquals(if (i == 2 || i == 6) "inverted" else "bounds", failure)
        assertEquals(before, snapshot())
    }
}

private fun assertDoubleSort(before: DoubleArray, after: DoubleArray, descending: Boolean = false) {
    check(before.size == after.size)
    for (i in 1 until after.size) {
        val comparison = after[i - 1].compareTo(after[i])
        check(if (descending) comparison >= 0 else comparison <= 0)
    }
    val originalBits = before.map { System.BitConverter.DoubleToInt64Bits(it) }
    val actualBits = after.map { System.BitConverter.DoubleToInt64Bits(it) }
    for (bits in originalBits) check(originalBits.count { it == bits } == actualBits.count { it == bits })
}

private fun assertFloatSort(before: FloatArray, after: FloatArray, descending: Boolean = false) {
    check(before.size == after.size)
    for (i in 1 until after.size) {
        val comparison = after[i - 1].compareTo(after[i])
        check(if (descending) comparison >= 0 else comparison <= 0)
    }
    val originalBits = before.map { System.BitConverter.SingleToInt32Bits(it) }
    val actualBits = after.map { System.BitConverter.SingleToInt32Bits(it) }
    for (bits in originalBits) check(originalBits.count { it == bits } == actualBits.count { it == bits })
}

class PrimitiveArraySortTests {
    @TestAttribute fun integralWholeSortsKeepSignedWidthAndNativeStorage() {
        val bytes = byteArrayOf(Byte.MAX_VALUE, 0, Byte.MIN_VALUE, -1, 1)
        val bn = bytes as System.Array
        bytes.sort()
        check(bytes.contentEquals(byteArrayOf(Byte.MIN_VALUE, -1, 0, 1, Byte.MAX_VALUE)))
        check((bytes as Any) === bn)
        assertNativePrimitiveValues(bn, bytes.map { it as Any? })
        val shorts = shortArrayOf(Short.MAX_VALUE, 0, Short.MIN_VALUE, -1, 1)
        val sn = shorts as System.Array
        shorts.sort()
        check(shorts.contentEquals(shortArrayOf(Short.MIN_VALUE, -1, 0, 1, Short.MAX_VALUE)))
        check((shorts as Any) === sn)
        assertNativePrimitiveValues(sn, shorts.map { it as Any? })
        val ints = intArrayOf(Int.MAX_VALUE, 0, Int.MIN_VALUE, -1, 1)
        val ni = ints as System.Array
        ints.sort()
        check(ints.contentEquals(intArrayOf(Int.MIN_VALUE, -1, 0, 1, Int.MAX_VALUE)))
        check((ints as Any) === ni)
        assertNativePrimitiveValues(ni, ints.map { it as Any? })
        val longs = longArrayOf(Long.MAX_VALUE, 0, Long.MIN_VALUE, -1, 1)
        val ln = longs as System.Array
        longs.sort()
        check(longs.contentEquals(longArrayOf(Long.MIN_VALUE, -1, 0, 1, Long.MAX_VALUE)))
        check((longs as Any) === ln)
        assertNativePrimitiveValues(ln, longs.map { it as Any? })
        val codes = intArrayOf(0xffff, 97, 0xd800, 0, 0xdfff)
        val chars = CharArray(codes.size) { codes[it].toChar() }
        val cn = chars as System.Array
        chars.sort()
        check(chars.map { it.code } == listOf(0, 97, 0xd800, 0xdfff, 0xffff))
        check((chars as Any) === cn)
        assertNativePrimitiveValues(cn, chars.map { it as Any? })
    }

    @TestAttribute fun allPrimitiveRangesSortOnlyTheirSelectedElements() {
        for (pattern in 0..5) {
            val size = 2048
            val input = IntArray(size) { i ->
                when (pattern) {
                    0 -> i
                    1 -> size - i
                    2 -> 7
                    3 -> if (i % 2 == 0) i else -i
                    4 -> (i * 7919) % 4093 - 2046
                    else -> i
                }
            }
            if (pattern == 5) for (right in 1 until size) {
                val middle = right / 2
                val saved = input[right]
                input[right] = input[middle]
                input[middle] = saved
            }
            val b = ByteArray(size) { input[it].toByte() }
            val be = b.toList().sorted(); b.sort(); check(b.toList() == be)
            val s = ShortArray(size) { input[it].toShort() }
            val se = s.toList().sorted(); s.sort(); check(s.toList() == se)
            val n = input.copyOf()
            val ne = n.toList().sorted(); n.sort(); check(n.toList() == ne)
            val l = LongArray(size) { input[it].toLong() }
            val le = l.toList().sorted(); l.sort(); check(l.toList() == le)
            val c = CharArray(size) { input[it].toChar() }
            val ce = c.toList().sorted(); c.sort(); check(c.toList() == ce)
            val f = FloatArray(size) { input[it].toFloat() }
            val fe = f.toList().sorted(); f.sort(); check(f.toList() == fe)
            val d = DoubleArray(size) { input[it].toDouble() }
            val de = d.toList().sorted(); d.sort(); check(d.toList() == de)
        }
        for (size in 0..8) for (from in 0..size) for (to in from..size) {
            val input = List(size) { (size - it) % 5 }
            val b = ByteArray(size) { input[it].toByte() }; b.sort(from, to)
            assertPrimitiveRange(input, b.map { it.toInt() }, from, to)
            val s = ShortArray(size) { input[it].toShort() }; s.sort(from, to)
            assertPrimitiveRange(input, s.map { it.toInt() }, from, to)
            val n = IntArray(size) { input[it] }; n.sort(from, to)
            assertPrimitiveRange(input, n.toList(), from, to)
            val l = LongArray(size) { input[it].toLong() }; l.sort(from, to)
            assertPrimitiveRange(input, l.map { it.toInt() }, from, to)
            val c = CharArray(size) { input[it].toChar() }; c.sort(from, to)
            assertPrimitiveRange(input, c.map { it.code }, from, to)
            val f = FloatArray(size) { input[it].toFloat() }; f.sort(from, to)
            assertPrimitiveRange(input, f.map { it.toInt() }, from, to)
            val d = DoubleArray(size) { input[it].toDouble() }; d.sort(from, to)
            assertPrimitiveRange(input, d.map { it.toInt() }, from, to)
        }
    }

    @TestAttribute fun invalidRangesHaveCommonExceptionPrecedenceAndDoNotWrite() {
        val b = byteArrayOf(3, 2, 1)
        assertInvalidPrimitiveSort({ from, to -> b.sort(from, to) }) { b.contentToString() }
        val s = shortArrayOf(3, 2, 1)
        assertInvalidPrimitiveSort({ from, to -> s.sort(from, to) }) { s.contentToString() }
        val n = intArrayOf(3, 2, 1)
        assertInvalidPrimitiveSort({ from, to -> n.sort(from, to) }) { n.contentToString() }
        val l = longArrayOf(3, 2, 1)
        assertInvalidPrimitiveSort({ from, to -> l.sort(from, to) }) { l.contentToString() }
        val c = charArrayOf('c', 'b', 'a')
        assertInvalidPrimitiveSort({ from, to -> c.sort(from, to) }) { c.contentToString() }
        val f = floatArrayOf(3f, 2f, 1f)
        assertInvalidPrimitiveSort({ from, to -> f.sort(from, to) }) { f.contentToString() }
        val d = doubleArrayOf(3.0, 2.0, 1.0)
        assertInvalidPrimitiveSort({ from, to -> d.sort(from, to) }) { d.contentToString() }
    }

    @TestAttribute fun floatingSortsPreserveTotalOrderPayloadsAndNativeViews() {
        val doubles = doubleArrayOf(
            System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L), 0.0, -0.0,
            Double.NEGATIVE_INFINITY, 2.0, Double.POSITIVE_INFINITY, -2.0,
            System.BitConverter.Int64BitsToDouble(Long.MIN_VALUE or 0x7ff8000000000002L))
        val floats = floatArrayOf(
            System.BitConverter.Int32BitsToSingle(0x7fc00001), 0f, -0f,
            Float.NEGATIVE_INFINITY, 2f, Float.POSITIVE_INFINITY, -2f,
            System.BitConverter.Int32BitsToSingle(Int.MIN_VALUE or 0x7fc00002))
        for (from in doubles.indices) for (to in from..doubles.size) {
            val d = doubles.copyOf(); val dn = d as System.Array
            d.sort(from, to)
            assertDoubleSort(doubles.copyOfRange(from, to), d.copyOfRange(from, to))
            check((d as Any) === dn)
            for (i in d.indices) {
                assertEquals(System.BitConverter.DoubleToInt64Bits(d[i]),
                    System.BitConverter.DoubleToInt64Bits(dn.GetValue(i) as Double))
                if (i < from || i >= to) assertEquals(System.BitConverter.DoubleToInt64Bits(doubles[i]),
                    System.BitConverter.DoubleToInt64Bits(d[i]))
            }
            val f = floats.copyOf(); val fn = f as System.Array
            f.sort(from, to)
            assertFloatSort(floats.copyOfRange(from, to), f.copyOfRange(from, to))
            check((f as Any) === fn)
            for (i in f.indices) {
                assertEquals(System.BitConverter.SingleToInt32Bits(f[i]),
                    System.BitConverter.SingleToInt32Bits(fn.GetValue(i) as Float))
                if (i < from || i >= to) assertEquals(System.BitConverter.SingleToInt32Bits(floats[i]),
                    System.BitConverter.SingleToInt32Bits(f[i]))
            }
        }
        val d = doubles.copyOf(); d.sort(); assertDoubleSort(doubles, d)
        val f = floats.copyOf(); f.sort(); assertFloatSort(floats, f)
        val expectedD = doubleArrayOf(Double.NEGATIVE_INFINITY, -2.0, -0.0, 0.0, 2.0, Double.POSITIVE_INFINITY)
        val expectedF = floatArrayOf(Float.NEGATIVE_INFINITY, -2f, -0f, 0f, 2f, Float.POSITIVE_INFINITY)
        for (i in expectedD.indices) {
            assertEquals(System.BitConverter.DoubleToInt64Bits(expectedD[i]), System.BitConverter.DoubleToInt64Bits(d[i]))
            assertEquals(System.BitConverter.SingleToInt32Bits(expectedF[i]), System.BitConverter.SingleToInt32Bits(f[i]))
        }
        check(d[6].isNaN() && d[7].isNaN() && f[6].isNaN() && f[7].isNaN())
    }

    @TestAttribute fun floatingDerivedSortingApisKeepInputsAndKotlinOrder() {
        val d = doubleArrayOf(Double.NaN, 1.0, 0.0, -0.0, Double.NEGATIVE_INFINITY)
        val db = d.copyOf()
        assertDoubleSort(d, d.sortedArray())
        assertDoubleSort(d, d.sortedArrayDescending(), true)
        assertDoubleSort(d, d.sorted().toDoubleArray())
        assertDoubleSort(d, d.sortedDescending().toDoubleArray(), true)
        check(d.contentEquals(db))
        val f = floatArrayOf(Float.NaN, 1f, 0f, -0f, Float.NEGATIVE_INFINITY)
        val fb = f.copyOf()
        assertFloatSort(f, f.sortedArray())
        assertFloatSort(f, f.sortedArrayDescending(), true)
        assertFloatSort(f, f.sorted().toFloatArray())
        assertFloatSort(f, f.sortedDescending().toFloatArray(), true)
        check(f.contentEquals(fb))
    }

    @TestAttribute fun emptySingletonAndDefaultRangesRemainValid() {
        byteArrayOf().sort(); shortArrayOf().sort(); intArrayOf().sort(); longArrayOf().sort()
        charArrayOf().sort(); floatArrayOf().sort(); doubleArrayOf().sort()
        byteArrayOf(1).sort(); shortArrayOf(1).sort(); intArrayOf(1).sort(); longArrayOf(1).sort()
        charArrayOf('a').sort(); floatArrayOf(1f).sort(); doubleArrayOf(1.0).sort()
        val b = byteArrayOf(3, 2, 1); b.sort(fromIndex = 1)
        check(b.contentEquals(byteArrayOf(3, 1, 2)))
        val s = shortArrayOf(3, 2, 1); s.sort(toIndex = 2)
        check(s.contentEquals(shortArrayOf(2, 3, 1)))
        val n = intArrayOf(3, 2, 1); n.sort(fromIndex = 1)
        check(n.contentEquals(intArrayOf(3, 1, 2)))
        val l = longArrayOf(3, 2, 1); l.sort(toIndex = 2)
        check(l.contentEquals(longArrayOf(2, 3, 1)))
        val c = charArrayOf('c', 'b', 'a'); c.sort(fromIndex = 1)
        check(c.contentEquals(charArrayOf('c', 'a', 'b')))
        val f = floatArrayOf(3f, 2f, 1f); f.sort(toIndex = 2)
        check(f.contentEquals(floatArrayOf(2f, 3f, 1f)))
        val d = doubleArrayOf(3.0, 2.0, 1.0); d.sort(fromIndex = 1)
        check(d.contentEquals(doubleArrayOf(3.0, 1.0, 2.0)))
    }

    @TestAttribute fun explicitlyInvokedClrSortRetainsItsNativeOrder() {
        val d = doubleArrayOf(Double.NaN, 1.0, 0.0, -0.0)
        val native = d as System.Array
        System.Array.Sort(native)
        check(d[0].isNaN() && (d as Any) === native)
        val f = floatArrayOf(Float.NaN, 1f, 0f, -0f)
        System.Array.Sort(f as System.Array)
        check(f[0].isNaN())
    }
}
