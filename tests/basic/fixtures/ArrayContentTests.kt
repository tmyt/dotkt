@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import kotlin.clr.ClrRef
import kotlin.clr.byref

private open class ArrayElementBoundNode {
    open fun label(): String = "node"
    override fun hashCode(): Int = 7
    override fun toString(): String = "node"
}
private class ArrayElementBoundItem : ArrayElementBoundNode() {
    override fun label(): String = "item"
}

@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> boundedArrayContent(operation: String): Any? {
    val node = ArrayElementBoundNode()
    val first = arrayOf(node) as Array<T>
    val second = arrayOf(node) as Array<T>
    return when (operation) {
        "equals" -> first.contentDeepEquals(second)
        "hash" -> first.contentDeepHashCode()
        else -> first.contentDeepToString()
    }
}

@Suppress("UNCHECKED_CAST")
private fun <T> nullArrayContent(operation: String): Any? {
    val first = arrayOfNulls<Any>(1) as Array<T>
    val second = arrayOfNulls<Any>(1) as Array<T>
    return when (operation) {
        "equals" -> first.contentDeepEquals(second)
        "hash" -> first.contentDeepHashCode()
        else -> first.contentDeepToString()
    }
}

@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> erasedArrayElementUses() {
    val values = arrayOf(ArrayElementBoundNode()) as Array<T>
    values[0]
    val direct: Any? = values[0]
    check(direct is ArrayElementBoundNode && direct !is ArrayElementBoundItem)
    check(conditionalArrayValue(values, true) === direct && conditionalArrayValue(values, false) === direct)
    check(blockArrayValue(values) === direct)
    val first = values[0]
    val alias = first
    check(first === alias)
    var mutable = alias
    mutable = values[0]
    check((mutable as Any?) === direct)
    check(mutable.label() == "node" && mutable.hashCode() == 7 && mutable.toString() == "node")
    var visits = 0
    for (value in values) {
        check(value.label() == "node" && value.hashCode() == 7 && value.toString() == "node")
        visits++
    }
    check(visits == 1)
}

@Suppress("UNCHECKED_CAST")
private fun <T> erasedNullElementUses() {
    val values = arrayOfNulls<Any>(1) as Array<T>
    values[0]
    val direct: Any? = values[0]
    val first = values[0]
    val second = values[0]
    check(first === second)
    var alias = first
    alias = values[0]
    check(direct == null && alias == null && alias !is ArrayElementBoundNode)
    check(conditionalArrayValue(values, true) == null && conditionalArrayValue(values, false) == null)
    check(blockArrayValue(values) == null)
    check(alias.hashCode() == 0 && alias.toString() == "null")
    for (value in values) check(value == null)
}

@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> typedArrayElementReturn(): T {
    val values = arrayOf(ArrayElementBoundNode()) as Array<T>
    return values[0]
}
private fun <T> typedArrayElementCall(value: T): Boolean = value === value
private fun <T> conditionalArrayValue(values: Array<T>, choose: Boolean): Any? {
    val value = if (choose) values[0] else values[0]
    return value
}
private fun <T> blockArrayValue(values: Array<T>): Any? {
    val value = run { val alias = values[0]; alias }
    return value
}
@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> typedExpressionArrayReturn(choose: Boolean, block: Boolean): T {
    val values = arrayOf(ArrayElementBoundNode()) as Array<T>
    return if (block) run { val alias = values[0]; alias }
        else if (choose) values[0] else values[0]
}
private fun <T> arrayReadSame(values: Array<T>, value: T): Boolean = values[0] === value
private fun <T> arrayReadTwice(values: Array<T>): Boolean = values[0] === values[0]
private fun <T> arraySavedAliasSame(values: Array<T>): Boolean {
    val first = values[0]
    val alias = first
    return first === alias
}
private fun <T> typedArrayValue(values: Array<T>): T = values[0]
private fun <T> typedArrayValueSame(values: Array<T>, value: T): Boolean = typedArrayValue(values) === value
private class ArrayIdentityElement {
    var equalityCalls = 0
    override fun equals(other: Any?): Boolean { equalityCalls++; return other is ArrayIdentityElement }
    override fun hashCode(): Int = 1
}
@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> consumeTypedArrayElement(): Boolean {
    val values = arrayOf(ArrayElementBoundNode()) as Array<T>
    return typedArrayElementCall<T>(values[0])
}
private fun <T> replaceNativeArrayElement(first: ClrRef<T>, second: ClrRef<T>, replacement: T) {
    first.value = replacement
    check(second.value == replacement)
}
private fun <T> nativeArrayElementLocal(values: Array<T>, replacement: T): Any? {
    var value = values[0]
    replaceNativeArrayElement(byref(value), byref(value), replacement)
    return value
}
private fun <T : ArrayElementBoundNode> arrayElementVirtualLabel(values: Array<T>): String {
    val value = values[0]
    return value.label()
}
private fun <A, B, T : System.Collections.Generic.List<B?>> frameExpandedArrayElement(
    first: A?, second: B?, values: Array<T>,
): Int {
    val value = values[0]
    @Suppress("UNCHECKED_CAST")
    val guarded = value as T
    check(first == null && second == null)
    return guarded.Count
}
private open class ArrayElementConstructorSink(val value: Any?)
private class ArrayElementConstructorRead<T>(values: Array<T>) : ArrayElementConstructorSink(values[0])
@Suppress("UNCHECKED_CAST")
private fun <T> erasedConstructorElement(): Any? {
    val values = arrayOfNulls<Any>(1) as Array<T>
    return ArrayElementConstructorRead(values).value
}
@Suppress("UNCHECKED_CAST")
private fun <T : ArrayElementBoundNode> invalidNativeArrayElementLocal(replacement: T): Any? {
    val values = arrayOf(ArrayElementBoundNode()) as Array<T>
    return nativeArrayElementLocal(values, replacement)
}
private inline fun <reified T> reifiedArrayElementBoundary(value: Any?): Any? = value as Array<T>
private fun arrayElementMustReject(body: () -> Unit) {
    var rejected = false
    try { body() } catch (failure: System.InvalidCastException) { rejected = true }
    check(rejected)
}

class GenericArrayElementTests {
    @TestAttribute fun boundedDeepEqualsUsesTheErasedElement() {
        check(boundedArrayContent<ArrayElementBoundItem>("equals") == true)
    }
    @TestAttribute fun boundedDeepHashUsesTheErasedElement() {
        check(boundedArrayContent<ArrayElementBoundItem>("hash") == 38)
    }
    @TestAttribute fun boundedDeepStringUsesTheErasedElement() {
        check(boundedArrayContent<ArrayElementBoundItem>("string") == "[node]")
    }
    @TestAttribute fun valueInstantiatedDeepEqualsRetainsNull() {
        check(nullArrayContent<Int>("equals") == true)
    }
    @TestAttribute fun valueInstantiatedDeepHashRetainsNull() {
        check(nullArrayContent<Int>("hash") == 31)
    }
    @TestAttribute fun valueInstantiatedDeepStringRetainsNull() {
        check(nullArrayContent<Int>("string") == "[null]")
    }
    @TestAttribute fun referenceInstantiatedDeepEqualsRetainsNull() {
        check(nullArrayContent<String>("equals") == true)
    }
    @TestAttribute fun referenceInstantiatedDeepHashRetainsNull() {
        check(nullArrayContent<String>("hash") == 31)
    }
    @TestAttribute fun referenceInstantiatedDeepStringRetainsNull() {
        check(nullArrayContent<String>("string") == "[null]")
    }

    @TestAttribute
    fun ordinaryGenericReadsAliasesAndLoopsDoNotForceConcreteExtraction() {
        erasedArrayElementUses<ArrayElementBoundItem>()
        erasedNullElementUses<Int>()
        erasedNullElementUses<String>()
        check(arrayElementVirtualLabel(arrayOf(ArrayElementBoundItem())) == "item")
    }

    @TestAttribute
    fun arrayIdentityUsesErasedValuesUntilARealTypedBoundary() {
        val values = arrayOf(1000)
        check(!arrayReadSame(values, 1000) && !arrayReadTwice(values))
        check(arraySavedAliasSame(values))
        check(typedArrayElementCall(1000) && typedArrayValueSame(values, 1000))
        val boxed: Any = 1000
        check(arrayReadSame(arrayOf(boxed), boxed) && arrayReadTwice(arrayOf(boxed)))
        val first = ArrayIdentityElement()
        val second = ArrayIdentityElement()
        val references = arrayOf(first)
        check(arrayReadSame(references, first) && !arrayReadSame(references, second))
        check(arrayReadTwice(references) && arraySavedAliasSame(references))
        check(typedArrayValueSame(references, first) && !typedArrayValueSame(references, second))
        check(first.equalityCalls == 0 && second.equalityCalls == 0)
    }

    @TestAttribute
    fun nullableExpandedMethodFramesRetainTheirGenericBoundArguments() {
        val values = System.Collections.Generic.List<Int?>()
        values.Add(7)
        check(frameExpandedArrayElement<String, Int, System.Collections.Generic.List<Int?>>(
            null, null, arrayOf(values)) == 1)
    }

    @TestAttribute
    fun constructorDelegationReadsStayErasedForAnObjectConsumer() {
        check(erasedConstructorElement<Int>() == null)
        check(erasedConstructorElement<String>() == null)
    }

    @TestAttribute
    fun typedConsumersAndNativeLocationsKeepTheirExactChecks() {
        arrayElementMustReject { typedArrayElementReturn<ArrayElementBoundItem>() }
        arrayElementMustReject { typedExpressionArrayReturn<ArrayElementBoundItem>(true, false) }
        arrayElementMustReject { typedExpressionArrayReturn<ArrayElementBoundItem>(false, false) }
        arrayElementMustReject { typedExpressionArrayReturn<ArrayElementBoundItem>(true, true) }
        arrayElementMustReject { consumeTypedArrayElement<ArrayElementBoundItem>() }
        arrayElementMustReject { arrayOf(ArrayElementBoundNode()) as Array<ArrayElementBoundItem> }
        arrayElementMustReject { reifiedArrayElementBoundary<ArrayElementBoundItem>(arrayOf(ArrayElementBoundNode())) }
        check(nativeArrayElementLocal(arrayOf(1), 2) == 2)
        check(nativeArrayElementLocal(arrayOf("before"), "after") == "after")
        arrayElementMustReject { invalidNativeArrayElementLocal(ArrayElementBoundItem()) }
    }
}

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
