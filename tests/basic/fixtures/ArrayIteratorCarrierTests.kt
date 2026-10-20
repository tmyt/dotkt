@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

private fun <T> arrayIteratorFirst(array: Array<T>): T = array.iterator().next()
private fun <T> nullableArrayIteratorFirst(array: Array<T?>): T? = array.iterator().next()
private fun <T> boundArrayIteratorFirst(array: Array<T>): T {
    val factory = array::iterator
    return factory().next()
}
private fun <T> boundNullableArrayIteratorFirst(array: Array<T?>): T? {
    val factory = array::iterator
    return factory().next()
}
private fun <T> projectedArrayIteratorFirst(array: Array<out T>): T = array.iterator().next()
private fun starArrayIteratorFirst(array: Array<*>): Any? = array.iterator().next()
private class ArrayIteratorOwner<T>(private val values: Array<T>) {
    fun first(): T = values.iterator().next()
}

class ArrayIteratorCarrierTests {
    @TestAttribute fun valueElementsUseTheNativeArrayReceiver() {
        val iterator = arrayOf(7, 9).iterator()
        check(iterator.hasNext() && iterator.hasNext())
        check(iterator.next() == 7 && iterator.next() == 9)
        check(!iterator.hasNext())
    }

    @TestAttribute fun referenceElementsUseTheNativeArrayReceiver() {
        val iterator = arrayOf("a", "b").iterator()
        check(iterator.next() == "a" && iterator.next() == "b")
        check(!iterator.hasNext())
    }

    @TestAttribute fun nullableValueElementsRetainTheirStorageCarrier() {
        val iterator = arrayOf<Int?>(7, null, 9).iterator()
        check(iterator.next() == 7)
        check(iterator.next() == null)
        check(iterator.next() == 9 && !iterator.hasNext())
    }

    @TestAttribute fun methodGenericFramesRetainTheArrayElement() {
        check(arrayIteratorFirst(arrayOf(7)) == 7)
        check(arrayIteratorFirst(arrayOf("a")) == "a")
    }

    @TestAttribute fun nullableGenericFramesRetainTheArrayStorage() {
        check(nullableArrayIteratorFirst<Int>(arrayOf<Int?>(7)) == 7)
        check(nullableArrayIteratorFirst<Int>(arrayOf<Int?>(null)) == null)
        check(nullableArrayIteratorFirst<String>(arrayOf<String?>("a")) == "a")
    }

    @TestAttribute fun ownerGenericFramesRetainTheArrayElement() {
        check(ArrayIteratorOwner(arrayOf(7)).first() == 7)
        check(ArrayIteratorOwner(arrayOf("a")).first() == "a")
    }

    @TestAttribute fun boundReferencesRetainOpenAndNullableGenericFrames() {
        check(boundArrayIteratorFirst(arrayOf(7)) == 7)
        check(boundArrayIteratorFirst(arrayOf("a")) == "a")
        check(boundNullableArrayIteratorFirst<Int>(arrayOf<Int?>(7)) == 7)
        check(boundNullableArrayIteratorFirst<Int>(arrayOf<Int?>(null)) == null)
        check(boundNullableArrayIteratorFirst<String>(arrayOf<String?>("a")) == "a")
    }

    @TestAttribute fun projectedReceiversRetainTheirAvailableStorageFrame() {
        check(projectedArrayIteratorFirst(arrayOf(7)) == 7)
        check(projectedArrayIteratorFirst(arrayOf("a")) == "a")
        check(starArrayIteratorFirst(arrayOf("a")) == "a")
        check(starArrayIteratorFirst(arrayOf<Int?>(7)) == 7)
        check(starArrayIteratorFirst(arrayOf<Int?>(null)) == null)
    }

    @TestAttribute fun primitiveArrayIteratorsRetainTheirSpecializedResults() {
        check(byteArrayOf(7.toByte()).iterator().nextByte() == 7.toByte())
        check(shortArrayOf(7.toShort()).iterator().nextShort() == 7.toShort())
        check(intArrayOf(7).iterator().nextInt() == 7)
        check(longArrayOf(7L).iterator().nextLong() == 7L)
        check(floatArrayOf(7.5f).iterator().nextFloat() == 7.5f)
        check(doubleArrayOf(7.5).iterator().nextDouble() == 7.5)
        check(charArrayOf('a').iterator().nextChar() == 'a')
        check(booleanArrayOf(true).iterator().nextBoolean())
    }

    @TestAttribute fun unsignedArrayIteratorsRetainUnsignedElementSemantics() {
        check(ubyteArrayOf(255u.toUByte()).iterator().next() == 255u.toUByte())
        check(ushortArrayOf(65535u.toUShort()).iterator().next() == 65535u.toUShort())
        check(uintArrayOf(UInt.MAX_VALUE).iterator().next() == UInt.MAX_VALUE)
        check(ulongArrayOf(ULong.MAX_VALUE).iterator().next() == ULong.MAX_VALUE)
    }

    @TestAttribute fun unsignedArrayObjectSlotsRemainOnNativeStorage() {
        val bytes = ubyteArrayOf(255u.toUByte())
        check(bytes.equals(bytes) && !bytes.equals(ubyteArrayOf(255u.toUByte())))
        check(bytes.hashCode() == (bytes as Any).hashCode())
        check(bytes.toString() == (bytes as Any).toString())
        val shorts = ushortArrayOf(65535u.toUShort())
        check(shorts.equals(shorts) && !shorts.equals(ushortArrayOf(65535u.toUShort())))
        check(shorts.hashCode() == (shorts as Any).hashCode())
        check(shorts.toString() == (shorts as Any).toString())
        val ints = uintArrayOf(UInt.MAX_VALUE)
        check(ints.equals(ints) && !ints.equals(uintArrayOf(UInt.MAX_VALUE)))
        check(ints.hashCode() == (ints as Any).hashCode())
        check(ints.toString() == (ints as Any).toString())
        val describe = ints::toString
        check(describe() == ints.toString())
        val longs = ulongArrayOf(ULong.MAX_VALUE)
        check(longs.equals(longs) && !longs.equals(ulongArrayOf(ULong.MAX_VALUE)))
        check(longs.hashCode() == (longs as Any).hashCode())
        check(longs.toString() == (longs as Any).toString())
    }

    @TestAttribute fun iteratorsRemainLiveAndHaveIndependentCursors() {
        val values = arrayOf("a", "b")
        val first = values.iterator()
        val second = values.iterator()
        values[0] = "changed"
        check(first.next() == "changed" && first.next() == "b")
        check(second.next() == "changed")
        check(!first.hasNext() && second.hasNext())
    }

    @TestAttribute fun emptyArrayIteratorsDoNotReportElements() {
        check(!emptyArray<Int>().iterator().hasNext())
        check(!intArrayOf().iterator().hasNext())
        check(!uintArrayOf().iterator().hasNext())
    }

    @TestAttribute fun boundIteratorReferencesUseThePhysicalReceiver() {
        val array = arrayOf(7)
        val primitive = intArrayOf(9)
        val unsigned = uintArrayOf(UInt.MAX_VALUE)
        val genericFactory = array::iterator
        val primitiveFactory = primitive::iterator
        val unsignedFactory = unsigned::iterator
        check(genericFactory().next() == 7)
        check(primitiveFactory().nextInt() == 9)
        check(unsignedFactory().next() == UInt.MAX_VALUE)
    }

    @TestAttribute fun unboundUnsignedIteratorReferencesUseNativeReceivers() {
        val bytes: (UByteArray) -> Iterator<UByte> = UByteArray::iterator
        val shorts: (UShortArray) -> Iterator<UShort> = UShortArray::iterator
        val ints: (UIntArray) -> Iterator<UInt> = UIntArray::iterator
        val longs: (ULongArray) -> Iterator<ULong> = ULongArray::iterator
        check(bytes(ubyteArrayOf(UByte.MAX_VALUE)).next() == UByte.MAX_VALUE)
        check(shorts(ushortArrayOf(UShort.MAX_VALUE)).next() == UShort.MAX_VALUE)
        check(ints(uintArrayOf(UInt.MAX_VALUE)).next() == UInt.MAX_VALUE)
        check(longs(ulongArrayOf(ULong.MAX_VALUE)).next() == ULong.MAX_VALUE)
    }

    @TestAttribute fun boundUnsignedIteratorReferencesUseNativeReceivers() {
        val bytes = ubyteArrayOf(UByte.MAX_VALUE)
        val shorts = ushortArrayOf(UShort.MAX_VALUE)
        val ints = uintArrayOf(UInt.MAX_VALUE)
        val longs = ulongArrayOf(ULong.MAX_VALUE)
        val byteIterator = bytes::iterator
        val shortIterator = shorts::iterator
        val intIterator = ints::iterator
        val longIterator = longs::iterator
        check(byteIterator().next() == UByte.MAX_VALUE)
        check(shortIterator().next() == UShort.MAX_VALUE)
        check(intIterator().next() == UInt.MAX_VALUE)
        check(longIterator().next() == ULong.MAX_VALUE)
    }

    @TestAttribute fun splicedUnsignedSearchesKeepNativeStorageAccess() {
        val bytes = ubyteArrayOf(1u, UByte.MAX_VALUE, UByte.MAX_VALUE)
        val shorts = ushortArrayOf(1u, UShort.MAX_VALUE, UShort.MAX_VALUE)
        val ints = uintArrayOf(1u, UInt.MAX_VALUE, UInt.MAX_VALUE)
        val longs = ulongArrayOf(1uL, ULong.MAX_VALUE, ULong.MAX_VALUE)
        check(bytes.indexOfFirst { it == UByte.MAX_VALUE } == 1)
        check(bytes.indexOfLast { it == UByte.MAX_VALUE } == 2)
        check(shorts.indexOfFirst { it == UShort.MAX_VALUE } == 1)
        check(shorts.indexOfLast { it == UShort.MAX_VALUE } == 2)
        check(ints.indexOfFirst { it == UInt.MAX_VALUE } == 1)
        check(ints.indexOfLast { it == UInt.MAX_VALUE } == 2)
        check(longs.indexOfFirst { it == ULong.MAX_VALUE } == 1)
        check(longs.indexOfLast { it == ULong.MAX_VALUE } == 2)
        check(uintArrayOf().indexOfFirst { true } == -1)
        check(uintArrayOf().indexOfLast { true } == -1)
    }
}
