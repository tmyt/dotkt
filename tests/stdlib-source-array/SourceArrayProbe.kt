@file:OptIn(ExperimentalUnsignedTypes::class)

package kotlin

private fun consumeNativeArray(value: System.Array): System.Array = value

public fun sourceByteArray(value: ByteArray) = consumeNativeArray(value)
public fun sourceShortArray(value: ShortArray) = consumeNativeArray(value)
public fun sourceIntArray(value: IntArray) = consumeNativeArray(value)
public fun sourceLongArray(value: LongArray) = consumeNativeArray(value)
public fun sourceFloatArray(value: FloatArray) = consumeNativeArray(value)
public fun sourceDoubleArray(value: DoubleArray) = consumeNativeArray(value)
public fun sourceCharArray(value: CharArray) = consumeNativeArray(value)
public fun sourceBooleanArray(value: BooleanArray) = consumeNativeArray(value)
public fun sourceUByteArray(value: UByteArray) = consumeNativeArray(value)
public fun sourceUShortArray(value: UShortArray) = consumeNativeArray(value)
public fun sourceUIntArray(value: UIntArray) = consumeNativeArray(value)
public fun sourceULongArray(value: ULongArray) = consumeNativeArray(value)
public fun <T> sourceGenericArray(value: Array<T>) = consumeNativeArray(value)
public fun sourceGenericStringArray(value: Array<String>) = sourceGenericArray(value)

private fun chooseArray(value: System.Array): String = "array"
private fun chooseArray(value: Any): String = "object"
private fun <T : System.Array> boundedArray(value: T): T = value

public fun sourceArrayNativeLength(value: CharArray): Int = value.Length
public fun sourceArrayBound(value: CharArray): CharArray = boundedArray(value)
public fun sourceArrayOverload(value: CharArray): String = chooseArray(value)
public fun sourceObjectOverload(value: Any): String = chooseArray(value)
public fun sourceArrayClear(value: CharArray) = System.Array.Clear(value, 0, value.size)
public fun sourceArrayClone(value: CharArray): CharArray = value.Clone() as CharArray
public fun sourceArrayExplicitCast(value: CharArray): System.Array = value as System.Array

internal open class SourceNativeArrayBox<T : System.Array>
internal class SourceArrayDerived : SourceNativeArrayBox<CharArray>()
internal fun sourceArrayDeclaredBound(value: SourceNativeArrayBox<CharArray>) = value

internal interface SourceNativeProducer { fun read(): System.Array }
internal class SourceCharProducer(private val value: CharArray) : SourceNativeProducer {
    override fun read(): CharArray = value
}
internal interface SourceNativePropertyProducer { val array: System.Array }
internal class SourceCharPropertyProducer(private val value: CharArray) : SourceNativePropertyProducer {
    override val array: CharArray get() = value
}
public fun sourceArrayCovariant(value: CharArray): System.Array = (SourceCharProducer(value) as SourceNativeProducer).read()
public fun sourceArrayCovariantProperty(value: CharArray): System.Array =
    (SourceCharPropertyProducer(value) as SourceNativePropertyProducer).array

private val sourceArrayTopLevel: System.Array = charArrayOf('t')
internal class SourceArrayInitializer(value: CharArray) { val array: System.Array = value }
private fun sourceArrayDefaultValue(value: System.Array = charArrayOf('d')) = value
public fun sourceArrayInitializers(value: CharArray): System.Array {
    val local: System.Array = SourceArrayInitializer(value).array
    check(sourceArrayTopLevel.Length == 1 && sourceArrayDefaultValue().Length == 1)
    return local
}
internal class SourceArrayDelegator(value: CharArray) : System.ICloneable by value
public fun sourceArrayDelegatedClone(value: CharArray): CharArray = SourceArrayDelegator(value).Clone() as CharArray
public fun sourceArrayLazy(value: CharArray): System.Array {
    val native: System.Array by lazy { value }
    return native
}
public fun sourceArrayDestructuring(value: CharArray): System.Array {
    val [native: System.Array, unused] = value to 0
    return native
}

public fun sourceArrayBoundClone(value: CharArray): CharArray {
    val clone = value::Clone
    return clone() as CharArray
}
public fun sourceArrayUnboundClone(value: CharArray): CharArray {
    val clone: (CharArray) -> Any = CharArray::Clone
    return clone(value) as CharArray
}
public fun sourceArrayUnboundLength(value: CharArray): Int {
    val length: (CharArray) -> Int = CharArray::Length
    return length(value)
}
internal typealias SourceChars = CharArray
public fun sourceArrayAliasClone(value: CharArray): CharArray {
    val clone = SourceChars::Clone
    return clone(value) as CharArray
}
public fun sourceUnsignedNativeLength(value: UByteArray): Int = value.Length
public fun <T> sourceGenericNativeLength(value: Array<T>): Int = value.Length
public fun sourceGenericStringNativeLength(value: Array<String>): Int = sourceGenericNativeLength(value)
