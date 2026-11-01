import NUnit.Framework.TestAttribute

private fun copyMustReject(block: () -> Unit) {
    var rejected = false
    try { block() } catch (_: Exception) { rejected = true }
    check(rejected)
}

@Suppress("UNCHECKED_CAST")
private fun <T> checkCopyDoesNotWiden() {
    val ints = intArrayOf(1, 2)
    val longs = longArrayOf(9L, 10L)
    val source = ints as Array<T>
    val destination = longs as Array<T>
    copyMustReject { source.copyInto(destination) }
    check(longs.contentEquals(longArrayOf(9L, 10L)))
    copyMustReject { destination + source }
    check(source.copyInto(destination, 0, 0, 0) === destination)
}

private class CopyElement(val number: Int)

class PortableArrayCopyTests {
    @TestAttribute fun genericCopiesAreShallowIndependentAndNullExtendible() {
        val element = CopyElement(7)
        val values = arrayOf(element)
        val copied = values.copyOf()
        check(copied !== values && copied[0] === element)
        val grown = values.copyOf(3)
        check(grown[0] === element && grown[1] == null && grown[2] == null)
        val range = values.copyOfRange(0, 1)
        check(range !== values && range[0] === element)
        copied[0] = CopyElement(8)
        check(values[0] === element && copied[0].number == 8)
        check(values.copyOf(0).isEmpty() && values.copyOfRange(1, 1).isEmpty())
        val nullable = arrayOf<String?>("first", null)
        check(nullable.copyOf().contentEquals(nullable))
        check(nullable.copyOf(3).contentEquals(arrayOf("first", null, null)))
        check(arrayOf(1, 2).copyOf(3).contentEquals(arrayOf<Int?>(1, 2, null)))
    }

    @TestAttribute fun genericPlusVariantsPreserveValuesAndDoNotMutateInputs() {
        val values = arrayOf("a", "b")
        check((values + "c").contentEquals(arrayOf("a", "b", "c")))
        check((values + listOf("c", "d")).contentEquals(arrayOf("a", "b", "c", "d")))
        check((values + arrayOf("c", "d")).contentEquals(arrayOf("a", "b", "c", "d")))
        check(values.plusElement("c").contentEquals(arrayOf("a", "b", "c")))
        check((values + emptyArray<String>()) !== values)
        check(values.contentEquals(arrayOf("a", "b")))
        var evaluations = ""
        fun receiver(): Array<String> { evaluations += "r"; return values }
        fun argument(): Array<String> { evaluations += "a"; return arrayOf("c") }
        check((receiver() + argument()).last() == "c" && evaluations == "ra")
        val copy: () -> Array<String> = values::copyOf
        check(copy().contentEquals(values) && copy() !== values)
    }

    @TestAttribute fun overlappingCopyIntoIsMemmoveInBothDirections() {
        val objects = arrayOf("a", "b", "c", "d")
        check(objects.copyInto(objects, 1, 0, 3) === objects)
        check(objects.contentEquals(arrayOf("a", "a", "b", "c")))
        objects.copyInto(objects, 0, 1, 4)
        check(objects.contentEquals(arrayOf("a", "b", "c", "c")))
        val ints = intArrayOf(1, 2, 3, 4)
        check(ints.copyInto(ints, 1, 0, 3) === ints)
        check(ints.contentEquals(intArrayOf(1, 1, 2, 3)))
        ints.copyInto(ints, 0, 1, 4)
        check(ints.contentEquals(intArrayOf(1, 2, 3, 3)))
    }

    @TestAttribute fun rangesRejectBeforeCopyingAndKeepExceptionPrecedence() {
        val values = intArrayOf(1, 2, 3)
        val destination = intArrayOf(9, 9, 9)
        fun invalid(from: Int, to: Int, offset: Int) {
            copyMustReject { values.copyInto(destination, offset, from, to) }
            check(destination.contentEquals(intArrayOf(9, 9, 9)))
        }
        invalid(-1, 1, 0)
        invalid(0, 4, 0)
        invalid(2, 1, 0)
        invalid(0, 1, -1)
        invalid(0, 3, 1)
        invalid(0, 1, Int.MAX_VALUE)
        invalid(3, 3, 4)
        check(values.copyInto(destination, 3, 3, 3) === destination)
        copyMustReject { values.copyOf(-1) }
        copyMustReject { values.copyOfRange(-1, 2) }
        copyMustReject { values.copyOfRange(2, 1) }
        var boundsFirst = false
        try { values.copyOfRange(5, 4) }
        catch (_: IndexOutOfBoundsException) { boundsFirst = true }
        check(boundsFirst)
        val references = arrayOf("a", "b", "c")
        copyMustReject { references.copyOf(-1) }
        copyMustReject { references.copyOfRange(-1, 2) }
        copyMustReject { references.copyOfRange(2, 1) }
        copyMustReject { references.copyInto(arrayOf("x"), Int.MAX_VALUE) }
    }

    @TestAttribute fun genericCopyAndAppendDoNotAcquireClrNumericConversions() {
        checkCopyDoesNotWiden<Int>()
    }

    @TestAttribute fun byteCopiesAndAppendsUsePortableBodies() {
        val a = byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE, 0)))
        check(a.copyOf(1).contentEquals(byteArrayOf(Byte.MIN_VALUE)))
        check(a.copyOfRange(1, 3).contentEquals(byteArrayOf(0, Byte.MAX_VALUE)))
        check((a + 1.toByte()).contentEquals(byteArrayOf(Byte.MIN_VALUE, 0, Byte.MAX_VALUE, 1)))
        check((a + listOf(1.toByte())).contentEquals(a + 1.toByte()))
        check((a + byteArrayOf(1)).contentEquals(a + 1.toByte()))
        check(a.toTypedArray().contentEquals(arrayOf(Byte.MIN_VALUE, 0.toByte(), Byte.MAX_VALUE)))
    }

    @TestAttribute fun shortCopiesAndAppendsUsePortableBodies() {
        val a = shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE, 0)))
        check(a.copyOf(1).contentEquals(shortArrayOf(Short.MIN_VALUE)))
        check(a.copyOfRange(1, 3).contentEquals(shortArrayOf(0, Short.MAX_VALUE)))
        check((a + 1.toShort()).contentEquals(shortArrayOf(Short.MIN_VALUE, 0, Short.MAX_VALUE, 1)))
        check((a + listOf(1.toShort())).contentEquals(a + 1.toShort()))
        check((a + shortArrayOf(1)).contentEquals(a + 1.toShort()))
        check(a.toTypedArray().contentEquals(arrayOf(Short.MIN_VALUE, 0.toShort(), Short.MAX_VALUE)))
    }

    @TestAttribute fun intCopiesAndAppendsUsePortableBodies() {
        val a = intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE, 0)))
        check(a.copyOf(1).contentEquals(intArrayOf(Int.MIN_VALUE)))
        check(a.copyOfRange(1, 3).contentEquals(intArrayOf(0, Int.MAX_VALUE)))
        check((a + 1).contentEquals(intArrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE, 1)))
        check((a + listOf(1)).contentEquals(a + 1))
        check((a + intArrayOf(1)).contentEquals(a + 1))
        check(a.toTypedArray().contentEquals(arrayOf(Int.MIN_VALUE, 0, Int.MAX_VALUE)))
    }

    @TestAttribute fun longCopiesAndAppendsUsePortableBodies() {
        val a = longArrayOf(Long.MIN_VALUE, 0L, Long.MAX_VALUE)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(longArrayOf(Long.MIN_VALUE, 0L, Long.MAX_VALUE, 0L)))
        check(a.copyOf(1).contentEquals(longArrayOf(Long.MIN_VALUE)))
        check(a.copyOfRange(1, 3).contentEquals(longArrayOf(0L, Long.MAX_VALUE)))
        check((a + 1L).contentEquals(longArrayOf(Long.MIN_VALUE, 0L, Long.MAX_VALUE, 1L)))
        check((a + listOf(1L)).contentEquals(a + 1L))
        check((a + longArrayOf(1L)).contentEquals(a + 1L))
        check(a.toTypedArray().contentEquals(arrayOf(Long.MIN_VALUE, 0L, Long.MAX_VALUE)))
    }

    @TestAttribute fun floatCopiesPreserveNanAndSignedZero() {
        val a = floatArrayOf(Float.NaN, -0.0f, 0.0f)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(floatArrayOf(Float.NaN, -0.0f, 0.0f, 0.0f)))
        check(a.copyOf(1).contentEquals(floatArrayOf(Float.NaN)))
        check(a.copyOfRange(1, 3).contentEquals(floatArrayOf(-0.0f, 0.0f)))
        check((a + 1.0f).contentEquals(floatArrayOf(Float.NaN, -0.0f, 0.0f, 1.0f)))
        check((a + listOf(1.0f)).contentEquals(a + 1.0f))
        check((a + floatArrayOf(1.0f)).contentEquals(a + 1.0f))
        val boxed = a.toTypedArray()
        check(boxed[0].isNaN() && boxed[1].toBits() == (-0.0f).toBits() && boxed[2].toBits() == 0.0f.toBits())
    }

    @TestAttribute fun doubleCopiesPreserveNanAndSignedZero() {
        val a = doubleArrayOf(Double.NaN, -0.0, 0.0)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(doubleArrayOf(Double.NaN, -0.0, 0.0, 0.0)))
        check(a.copyOf(1).contentEquals(doubleArrayOf(Double.NaN)))
        check(a.copyOfRange(1, 3).contentEquals(doubleArrayOf(-0.0, 0.0)))
        check((a + 1.0).contentEquals(doubleArrayOf(Double.NaN, -0.0, 0.0, 1.0)))
        check((a + listOf(1.0)).contentEquals(a + 1.0))
        check((a + doubleArrayOf(1.0)).contentEquals(a + 1.0))
        val boxed = a.toTypedArray()
        check(boxed[0].isNaN() && boxed[1].toBits() == (-0.0).toBits() && boxed[2].toBits() == 0.0.toBits())
    }

    @TestAttribute fun booleanCopiesAndAppendsUsePortableBodies() {
        val a = booleanArrayOf(true, false)
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(3).contentEquals(booleanArrayOf(true, false, false)))
        check(a.copyOf(1).contentEquals(booleanArrayOf(true)))
        check(a.copyOfRange(1, 2).contentEquals(booleanArrayOf(false)))
        check((a + true).contentEquals(booleanArrayOf(true, false, true)))
        check((a + listOf(true)).contentEquals(a + true))
        check((a + booleanArrayOf(true)).contentEquals(a + true))
        check(a.toTypedArray().contentEquals(arrayOf(true, false)))
    }

    @TestAttribute fun charCopiesKeepNullCharactersAndZeroFill() {
        val a = charArrayOf('\u0000', 'x', '\uffff')
        check(a.copyOf().contentEquals(a) && a.copyOf() !== a)
        check(a.copyOf(4).contentEquals(charArrayOf('\u0000', 'x', '\uffff', '\u0000')))
        check(a.copyOf(1).contentEquals(charArrayOf('\u0000')))
        check(a.copyOfRange(1, 3).contentEquals(charArrayOf('x', '\uffff')))
        check((a + 'y').contentEquals(charArrayOf('\u0000', 'x', '\uffff', 'y')))
        check((a + listOf('y')).contentEquals(a + 'y'))
        check((a + charArrayOf('y')).contentEquals(a + 'y'))
        check(a.toTypedArray().contentEquals(arrayOf('\u0000', 'x', '\uffff')))
    }
}
