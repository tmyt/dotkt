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
