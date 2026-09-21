@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

private fun <T> arrayIteratorFirst(array: Array<T>): T = array.iterator().next()
private fun <T> nullableArrayIteratorFirst(array: Array<T?>): T? = array.iterator().next()
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
}
