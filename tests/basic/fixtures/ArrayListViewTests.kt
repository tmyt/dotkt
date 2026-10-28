@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

private class ArrayViewToken(val label: String)

private fun <T> assertArrayListView(view: List<T>, before: T, after: T, change: () -> Unit) {
    check(view is RandomAccess && view.size == 3 && !view.isEmpty())
    check(view[0] == before && view[2] == before)
    val snapshot = view.toList()
    val iterator = view.iterator()
    val backwards = view.listIterator(view.size)
    val subList = view.subList(0, 2)
    val nested = view.subList(0, 3).subList(1, 3)
    change()
    check(view[0] == after && view[2] == after)
    check(subList[0] == after && nested[1] == after)
    check(iterator.next() == after && backwards.previous() == after)
    check(iterator.next() == view[1] && iterator.next() == after && !iterator.hasNext())
    check(view.contains(after) && view.indexOf(after) == 0 && view.lastIndexOf(after) == 2)
    check(snapshot[0] == before && snapshot[2] == before)
    repeat(2) {
        var exhausted = false
        try { iterator.next() } catch (_: NoSuchElementException) { exhausted = true }
        check(exhausted && !iterator.hasNext())
    }
}

class ArrayListViewTests {
    @TestAttribute
    fun genericArraysRetainBackingStorageForReferenceNullableAndValueElements() {
        val strings = arrayOf("a", "middle", "a")
        assertArrayListView(strings.asList(), "a", "z") { strings[0] = "z"; strings[2] = "z" }
        val nullable = arrayOf<Int?>(null, 2, null)
        assertArrayListView(nullable.asList(), null, 7) { nullable[0] = 7; nullable[2] = 7 }
        val numbers = arrayOf(1, 2, 1)
        assertArrayListView(numbers.asList(), 1, 3) { numbers[0] = 3; numbers[2] = 3 }
        val first = ArrayViewToken("first")
        val last = ArrayViewToken("last")
        val objects = arrayOf(first, ArrayViewToken("middle"), first)
        val view = objects.asList()
        assertArrayListView(view, first, last) { objects[0] = last; objects[2] = last }
        check(view[0] === last && view[2] === last)
        val one = System.TimeSpan(0, 0, 1)
        val three = System.TimeSpan(0, 0, 3)
        val durations = arrayOf(one, System.TimeSpan(0, 0, 2), one)
        assertArrayListView(durations.asList(), one, three) { durations[0] = three; durations[2] = three }
    }

    @TestAttribute
    fun allPrimitiveArrayViewsRemainLiveThroughIteratorsAndSubLists() {
        val bytes = byteArrayOf(1, 2, 1)
        assertArrayListView(bytes.asList(), 1.toByte(), 3.toByte()) { bytes[0] = 3; bytes[2] = 3 }
        val shorts = shortArrayOf(1, 2, 1)
        assertArrayListView(shorts.asList(), 1.toShort(), 3.toShort()) { shorts[0] = 3; shorts[2] = 3 }
        val ints = intArrayOf(1, 2, 1)
        assertArrayListView(ints.asList(), 1, 3) { ints[0] = 3; ints[2] = 3 }
        val longs = longArrayOf(1, 2, 1)
        assertArrayListView(longs.asList(), 1L, 3L) { longs[0] = 3; longs[2] = 3 }
        val floats = floatArrayOf(1.0f, 2.0f, 1.0f)
        assertArrayListView(floats.asList(), 1.0f, 3.0f) { floats[0] = 3.0f; floats[2] = 3.0f }
        val doubles = doubleArrayOf(1.0, 2.0, 1.0)
        assertArrayListView(doubles.asList(), 1.0, 3.0) { doubles[0] = 3.0; doubles[2] = 3.0 }
        val booleans = booleanArrayOf(false, true, false)
        assertArrayListView(booleans.asList(), false, true) { booleans[0] = true; booleans[2] = true }
        val chars = charArrayOf('a', 'm', 'a')
        assertArrayListView(chars.asList(), 'a', 'z') { chars[0] = 'z'; chars[2] = 'z' }
    }

    @TestAttribute
    fun allUnsignedArrayViewsRemainLiveThroughIteratorsAndSubLists() {
        val bytes = ubyteArrayOf(1u, 2u, 1u)
        assertArrayListView(bytes.asList(), 1u.toUByte(), UByte.MAX_VALUE) {
            bytes[0] = UByte.MAX_VALUE; bytes[2] = UByte.MAX_VALUE
        }
        val shorts = ushortArrayOf(1u, 2u, 1u)
        assertArrayListView(shorts.asList(), 1u.toUShort(), UShort.MAX_VALUE) {
            shorts[0] = UShort.MAX_VALUE; shorts[2] = UShort.MAX_VALUE
        }
        val ints = uintArrayOf(1u, 2u, 1u)
        assertArrayListView(ints.asList(), 1u, UInt.MAX_VALUE) { ints[0] = UInt.MAX_VALUE; ints[2] = UInt.MAX_VALUE }
        val longs = ulongArrayOf(1uL, 2uL, 1uL)
        assertArrayListView(longs.asList(), 1uL, ULong.MAX_VALUE) { longs[0] = ULong.MAX_VALUE; longs[2] = ULong.MAX_VALUE }
    }

    @TestAttribute
    fun floatingViewsFindNaNsAndDistinguishSignedZeroAfterBackingWrites() {
        val floats = floatArrayOf(0.0f, -0.0f, Float.NaN, Float.NaN)
        val f = floats.asList()
        check(f.contains(Float.NaN) && f.indexOf(Float.NaN) == 2 && f.lastIndexOf(Float.NaN) == 3)
        check(f.indexOf(0.0f) == 0 && f.lastIndexOf(0.0f) == 0 && f.indexOf(-0.0f) == 1)
        floats[0] = -0.0f; floats[3] = 0.0f
        check(f.indexOf(0.0f) == 3 && f.lastIndexOf(-0.0f) == 1 && f.lastIndexOf(Float.NaN) == 2)
        val doubles = doubleArrayOf(0.0, -0.0, Double.NaN, Double.NaN)
        val d = doubles.asList()
        check(d.contains(Double.NaN) && d.indexOf(Double.NaN) == 2 && d.lastIndexOf(Double.NaN) == 3)
        check(d.indexOf(0.0) == 0 && d.lastIndexOf(0.0) == 0 && d.indexOf(-0.0) == 1)
        doubles[0] = -0.0; doubles[3] = 0.0
        check(d.indexOf(0.0) == 3 && d.lastIndexOf(-0.0) == 1 && d.lastIndexOf(Double.NaN) == 2)
    }

    @TestAttribute
    fun allEmptyViewsPreserveListBoundsAndRepeatedIteratorExhaustion() {
        val views = listOf(
            emptyArray<String>().asList(), ByteArray(0).asList(), ShortArray(0).asList(),
            IntArray(0).asList(), LongArray(0).asList(), FloatArray(0).asList(), DoubleArray(0).asList(),
            BooleanArray(0).asList(), CharArray(0).asList(), UByteArray(0).asList(), UShortArray(0).asList(),
            UIntArray(0).asList(), ULongArray(0).asList()
        )
        check(views.size == 13)
        for (view in views) {
            check(view is RandomAccess && view.isEmpty() && view.size == 0)
            check(view.subList(0, 0).isEmpty() && view.listIterator().nextIndex() == 0)
            val iterator = view.iterator()
            repeat(2) {
                var exhausted = false
                try { iterator.next() } catch (_: NoSuchElementException) { exhausted = true }
                check(exhausted && !iterator.hasNext())
            }
            var outOfBounds = false
            try { view[0] } catch (_: IndexOutOfBoundsException) { outOfBounds = true }
            check(outOfBounds)
            outOfBounds = false
            try { view[-1] } catch (_: IndexOutOfBoundsException) { outOfBounds = true }
            check(outOfBounds)
        }
    }
}
