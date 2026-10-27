@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute

private class ContentElement(private val value: Int) {
    override fun equals(other: Any?): Boolean = other is ContentElement && value == other.value
    override fun hashCode(): Int = value * 17
    override fun toString(): String = "element:$value"
}

class ArrayCollectionWitnessTests {
    @TestAttribute
    fun arrayProjectionMembershipAndIterationRetainTheirSourceViews() {
        val first = arrayOf("first")
        val second = arrayOf(2, 3)
        val seen = mutableListOf<Array<*>>()
        check(first !in seen)
        seen.add(first)
        seen.add(second)
        check(first in seen && second in seen && arrayOf("first") !in seen)
        var elements = 0
        for (array in seen) elements += array.size
        check(elements == 3)
        val nullable = mutableListOf<Array<*>?>()
        nullable.add(null)
        nullable.add(second)
        check(null in nullable && second in nullable && first !in nullable)
        val nested = arrayOf<Array<*>?>(first, null)
        val nesting = mutableListOf<Array<Array<*>?>>()
        nesting.add(nested)
        check(nested in nesting && arrayOf<Array<*>?>(first, null) !in nesting)
        val concrete = mutableListOf<Array<String>>(first)
        check(first in concrete && arrayOf("first") !in concrete)
        val map = mutableMapOf<Array<*>, String>(second to "second")
        check(map[second] == "second" && map[first] == null)
    }
}

private fun primitiveContentValues(): Array<Any?> = arrayOf(
    byteArrayOf(-1, 2), shortArrayOf(-3, 4), intArrayOf(-5, 6), longArrayOf(-7L, 8L),
    floatArrayOf(1.5f, 2.5f), doubleArrayOf(3.5, 4.5), booleanArrayOf(true, false), charArrayOf('x', 'y'),
    ubyteArrayOf(255u, 2u), ushortArrayOf(65535u, 4u),
    uintArrayOf(UInt.MAX_VALUE, 6u), ulongArrayOf(ULong.MAX_VALUE, 8uL),
)

class ArrayContentTests {
    @TestAttribute
    fun nullableAndEmptyDeepContentsRetainTheirContracts() {
        val absent: Array<Any?>? = null
        val empty = emptyArray<Any?>()
        check(absent.contentDeepEquals(null))
        check(!absent.contentDeepEquals(empty) && !empty.contentDeepEquals(absent))
        check(absent.contentDeepHashCode() == 0 && absent.contentDeepToString() == "null")
        check(empty.contentDeepEquals(emptyArray<Any?>()))
        check(empty.contentDeepHashCode() == 1 && empty.contentDeepToString() == "[]")
    }

    @TestAttribute
    fun nestedElementsUseTheirKotlinEqualsHashAndRendering() {
        val first = arrayOf<Any?>(arrayOf(ContentElement(3), null), "text")
        val second = arrayOf<Any?>(arrayOf(ContentElement(3), null), "text")
        val different = arrayOf<Any?>(arrayOf(ContentElement(4), null), "text")
        check(first !== second && first.contentDeepEquals(second))
        check(!first.contentDeepEquals(different))
        val nestedHash = 31 * (31 + 51)
        check(first.contentDeepHashCode() == 31 * (31 + nestedHash) + "text".hashCode())
        check(first.contentDeepHashCode() == second.contentDeepHashCode())
        check(first.contentDeepToString() == "[[element:3, null], text]")
    }

    @TestAttribute
    fun deepOperationsVisitEveryPrimitiveAndUnsignedArrayKind() {
        val first = primitiveContentValues()
        val second = primitiveContentValues()
        check(first.contentDeepEquals(second))
        check(first.contentDeepHashCode() == second.contentDeepHashCode())
        check(first.contentDeepToString() ==
            "[[-1, 2], [-3, 4], [-5, 6], [-7, 8], [1.5, 2.5], [3.5, 4.5], " +
            "[True, False], [x, y], [255, 2], [65535, 4], [4294967295, 6], [18446744073709551615, 8]]") {
            "Deep rendering: ${first.contentDeepToString()}"
        }
        // Derive the nested-List oracle independently of every array hash implementation.
        val componentLists = listOf<List<*>>(
            listOf<Byte>(-1, 2), listOf<Short>(-3, 4), listOf(-5, 6), listOf(-7L, 8L),
            listOf(1.5f, 2.5f), listOf(3.5, 4.5), listOf(true, false), listOf('x', 'y'),
            listOf<UByte>(255u, 2u), listOf<UShort>(65535u, 4u),
            listOf<UInt>(UInt.MAX_VALUE, 6u), listOf<ULong>(ULong.MAX_VALUE, 8uL),
        )
        var expected = 1
        for (component in componentLists) expected = 31 * expected + component.hashCode()
        check(expected == componentLists.hashCode())
        check(first.contentDeepHashCode() == expected) {
            "Deep hash: actual=${first.contentDeepHashCode()}, expected=$expected"
        }
        for (i in first.indices) {
            check(arrayOf<Any?>(first[i]).contentDeepHashCode() == 31 + componentLists[i].hashCode()) {
                "Deep component hash disagrees with its independent List at index $i"
            }
        }
        second[10] = uintArrayOf(0u, 6u)
        check(!first.contentDeepEquals(second)) {
            "Deep equality retained a changed UIntArray: ${second.contentDeepToString()}"
        }
    }

    @TestAttribute
    fun deepRenderingDetectsCyclesButNotRepeatedAcyclicChildren() {
        val self = arrayOfNulls<Any>(1)
        self[0] = self
        check(self.contentDeepToString() == "[[...]]")
        val left = arrayOfNulls<Any>(1)
        val right = arrayOfNulls<Any>(1)
        left[0] = right
        right[0] = left
        check(left.contentDeepToString() == "[[[...]]]")
        val child = arrayOf<Any?>("shared", null)
        check(arrayOf(child, child).contentDeepToString() == "[[shared, null], [shared, null]]")
    }

    @TestAttribute
    fun floatingContentEqualityUsesStructuralRatherThanIeeeEquality() {
        val floatNaNs = floatArrayOf(System.BitConverter.Int32BitsToSingle(0x7fc00001))
        val otherFloatNaNs = floatArrayOf(System.BitConverter.Int32BitsToSingle(0x7fc00002))
        check(floatNaNs !== otherFloatNaNs && floatNaNs.contentEquals(otherFloatNaNs))
        check(!floatArrayOf(-0.0f).contentEquals(floatArrayOf(0.0f)))
        val doubleNaNs = doubleArrayOf(System.BitConverter.Int64BitsToDouble(0x7ff8000000000001L))
        val otherDoubleNaNs = doubleArrayOf(System.BitConverter.Int64BitsToDouble(0x7ff8000000000002L))
        check(doubleNaNs !== otherDoubleNaNs && doubleNaNs.contentEquals(otherDoubleNaNs))
        check(!doubleArrayOf(-0.0).contentEquals(doubleArrayOf(0.0)))
        check(floatArrayOf(Float.NEGATIVE_INFINITY, 3.0f).contentEquals(floatArrayOf(Float.NEGATIVE_INFINITY, 3.0f)))
        check(doubleArrayOf(Double.POSITIVE_INFINITY, 3.0).contentEquals(doubleArrayOf(Double.POSITIVE_INFINITY, 3.0)))
        check(!floatArrayOf(1.0f).contentEquals(floatArrayOf(2.0f)))
        check(!doubleArrayOf(1.0).contentEquals(doubleArrayOf()))
        val absentFloat: FloatArray? = null
        val absentDouble: DoubleArray? = null
        check(absentFloat.contentEquals(null) && !absentFloat.contentEquals(floatArrayOf()))
        check(absentDouble.contentEquals(null) && !doubleArrayOf().contentEquals(absentDouble))
        check(floatNaNs.contentEquals(floatNaNs) && doubleNaNs.contentEquals(doubleNaNs))
        check(arrayOf(Float.NaN).contentEquals(arrayOf(Float.NaN)))
        check(!arrayOf(-0.0).contentEquals(arrayOf(0.0)))
        check(arrayOf<Any?>(floatNaNs, doubleNaNs).contentDeepEquals(arrayOf<Any?>(otherFloatNaNs, otherDoubleNaNs)))
        check(!arrayOf<Any?>(floatArrayOf(-0.0f)).contentDeepEquals(arrayOf<Any?>(floatArrayOf(0.0f))))
    }
}
