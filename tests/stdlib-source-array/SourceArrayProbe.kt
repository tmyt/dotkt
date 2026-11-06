@file:OptIn(ExperimentalUnsignedTypes::class)

package kotlin

private fun consumeNativeArray(value: System.Array): System.Array = value

internal fun sourceByteArray(value: ByteArray) = consumeNativeArray(value)
internal fun sourceShortArray(value: ShortArray) = consumeNativeArray(value)
internal fun sourceIntArray(value: IntArray) = consumeNativeArray(value)
internal fun sourceLongArray(value: LongArray) = consumeNativeArray(value)
internal fun sourceFloatArray(value: FloatArray) = consumeNativeArray(value)
internal fun sourceDoubleArray(value: DoubleArray) = consumeNativeArray(value)
internal fun sourceCharArray(value: CharArray) = consumeNativeArray(value)
internal fun sourceBooleanArray(value: BooleanArray) = consumeNativeArray(value)
internal fun sourceUByteArray(value: UByteArray) = consumeNativeArray(value)
internal fun sourceUShortArray(value: UShortArray) = consumeNativeArray(value)
internal fun sourceUIntArray(value: UIntArray) = consumeNativeArray(value)
internal fun sourceULongArray(value: ULongArray) = consumeNativeArray(value)
internal fun <T> sourceGenericArray(value: Array<T>) = consumeNativeArray(value)

private fun chooseArray(value: System.Array): String = "array"
private fun chooseArray(value: Any): String = "object"
private fun <T : System.Array> boundedArray(value: T): T = value

internal fun sourceArrayNativeLength(value: CharArray): Int = value.Length
internal fun sourceArrayBound(value: CharArray): CharArray = boundedArray(value)
internal fun sourceArrayOverload(value: CharArray): String = chooseArray(value)
internal fun sourceObjectOverload(value: Any): String = chooseArray(value)
internal fun sourceArrayClear(value: CharArray) = System.Array.Clear(value, 0, value.size)
internal fun sourceArrayClone(value: CharArray): CharArray = value.Clone() as CharArray
