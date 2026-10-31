@file:OptIn(ExperimentalUnsignedTypes::class)

import NUnit.Framework.TestAttribute
import kotlinlike.arraycontent.dataClassArrayMemberToString
import kotlinlike.arraycontent.dataClassArrayMemberHashCode

private data class IntegralArrayRecord(val bytes: ByteArray, val shorts: ShortArray, val ints: IntArray, val longs: LongArray)
private data class OtherArrayRecord(val booleans: BooleanArray, val chars: CharArray, val floats: FloatArray, val doubles: DoubleArray)
private data class GenericArrayRecord<T>(val values: Array<T>)
private data class StarArrayRecord(val values: Array<*>)
private data class NullableArrayRecord(val ints: IntArray?, val names: Array<String?>?)
private value class ArrayValueRecord(val values: IntArray)

class DataClassArrayContentTests {
    @TestAttribute
    fun generatedIntegralArrayMembersUseContentOperations() {
        val value = IntegralArrayRecord(byteArrayOf(-1, 127), shortArrayOf(-32768, 32767), intArrayOf(1, 2), longArrayOf(Long.MIN_VALUE, Long.MAX_VALUE))
        check(value.toString() == "IntegralArrayRecord(bytes=[-1, 127], shorts=[-32768, 32767], ints=[1, 2], longs=[-9223372036854775808, 9223372036854775807])")
        check(value.hashCode() == ((value.bytes.contentHashCode() * 31 + value.shorts.contentHashCode()) * 31 + value.ints.contentHashCode()) * 31 + value.longs.contentHashCode())
        check(GenericArrayRecord(intArrayOf(1, 2).toTypedArray()).hashCode() == 994)
        val wrapped = ArrayValueRecord(intArrayOf(1, 2))
        check(wrapped.toString() == "ArrayValueRecord(values=[1, 2])")
        check(wrapped.hashCode() == 994)
    }

    @TestAttribute
    fun generatedBooleanCharacterAndFloatingArrayMembersUseContentOperations() {
        val value = OtherArrayRecord(booleanArrayOf(true, false), charArrayOf('a', 'z'), floatArrayOf(Float.NaN, -0.0f), doubleArrayOf(Double.NaN, -0.0))
        check(value.toString() == "OtherArrayRecord(booleans=${value.booleans.contentToString()}, chars=[a, z], floats=${value.floats.contentToString()}, doubles=${value.doubles.contentToString()})")
        check(value.hashCode() == ((value.booleans.contentHashCode() * 31 + value.chars.contentHashCode()) * 31 + value.floats.contentHashCode()) * 31 + value.doubles.contentHashCode())
        val negativeZero = arrayOf(-0.0)
        val nan = arrayOf(Double.NaN)
        check(GenericArrayRecord(negativeZero).hashCode() == negativeZero.contentHashCode())
        check(GenericArrayRecord(nan).hashCode() == nan.contentHashCode())
    }

    @TestAttribute
    fun generatedGenericArrayMembersRetainElementTypes() {
        val text = GenericArrayRecord(arrayOf("one", "two"))
        check(text.toString() == "GenericArrayRecord(values=[one, two])")
        check(text.hashCode() == text.values.contentHashCode())
        val numbers = GenericArrayRecord(arrayOf(7, 8))
        check(numbers.toString() == "GenericArrayRecord(values=[7, 8])")
        check(numbers.hashCode() == 1186)
        val unsigned = GenericArrayRecord(arrayOf(1u, UInt.MAX_VALUE))
        check(unsigned.toString() == "GenericArrayRecord(values=[1, 4294967295])")
        check(unsigned.hashCode() == unsigned.values.contentHashCode())
        val stars = StarArrayRecord(arrayOf(null, "x"))
        check(stars.toString() == "StarArrayRecord(values=[null, x])")
        check(stars.hashCode() == stars.values.contentHashCode())
    }

    @TestAttribute
    fun generatedNullableArrayMembersRetainNullAndEmptyContent() {
        check(NullableArrayRecord(null, null).toString() == "NullableArrayRecord(ints=null, names=null)")
        check(NullableArrayRecord(null, null).hashCode() == 0)
        val value = NullableArrayRecord(intArrayOf(), arrayOf(null, "x"))
        check(value.toString() == "NullableArrayRecord(ints=[], names=[null, x])")
        check(value.hashCode() == 31 + value.names.contentHashCode())
    }

    @TestAttribute
    fun sourceArrayViewsChoosePolicyRatherThanRuntimeArrayIdentity() {
        @Suppress("UNCHECKED_CAST")
        val unsigned = intArrayOf(-1, 0) as Array<UInt>
        val record = GenericArrayRecord(unsigned)
        check(record.toString() == "GenericArrayRecord(values=${unsigned.contentToString()})")
        check(record.hashCode() == unsigned.contentHashCode())
    }

    @TestAttribute
    fun similarlyNamedOrdinaryFunctionsRemainOrdinaryCalls() {
        check(dataClassArrayMemberToString(intArrayOf(1)) == "ordinary")
        check(dataClassArrayMemberHashCode(intArrayOf(1)) == 123)
    }
}
