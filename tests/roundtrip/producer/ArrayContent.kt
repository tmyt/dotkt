@file:Suppress("NOTHING_TO_INLINE")
@file:OptIn(ExperimentalUnsignedTypes::class)

package roundtrip.arraycontent

fun <T> deepEqualsInLibrary(first: Array<out T>?, second: Array<out T>?): Boolean = first.contentDeepEquals(second)
fun <T> deepHashInLibrary(value: Array<out T>?): Int = value.contentDeepHashCode()
fun <T> deepStringInLibrary(value: Array<out T>?): String = value.contentDeepToString()

open class ImportedArrayBound {
    open fun label(): String = "node"
    override fun hashCode(): Int = 7
    override fun toString(): String = "node"
}
class ImportedArrayItem : ImportedArrayBound() {
    override fun label(): String = "item"
}
@Suppress("UNCHECKED_CAST")
fun <T : ImportedArrayBound> importedErasedArrayContent(operation: String): Any? {
    val node = ImportedArrayBound()
    val first = arrayOf(node) as Array<T>
    val second = arrayOf(node) as Array<T>
    return when (operation) {
        "equals" -> first.contentDeepEquals(second)
        "hash" -> first.contentDeepHashCode()
        else -> first.contentDeepToString()
    }
}
@Suppress("UNCHECKED_CAST")
fun <T> importedNullArrayContent(operation: String): Any? {
    val first = arrayOfNulls<Any>(1) as Array<T>
    val second = arrayOfNulls<Any>(1) as Array<T>
    return when (operation) {
        "equals" -> first.contentDeepEquals(second)
        "hash" -> first.contentDeepHashCode()
        else -> first.contentDeepToString()
    }
}
fun <T : ImportedArrayBound> importedArrayVirtualLabel(values: Array<T>): String {
    val value = values[0]
    return value.label()
}

inline fun <T> inlineDeepEquals(first: Array<out T>?, second: Array<out T>?, transform: (Boolean) -> Boolean): Boolean = transform(first.contentDeepEquals(second))
inline fun <T> inlineDeepHash(value: Array<out T>?, transform: (Int) -> Int): Int = transform(value.contentDeepHashCode())
inline fun <T> inlineDeepString(value: Array<out T>?, transform: (String) -> String): String = transform(value.contentDeepToString())

fun <T> boundDeepEquals(value: Array<out T>?): (Array<out T>?) -> Boolean = value::contentDeepEquals
fun <T> boundDeepHash(value: Array<out T>?): () -> Int = value::contentDeepHashCode
fun <T> boundDeepString(value: Array<out T>?): () -> String = value::contentDeepToString

fun floatContentsInLibrary(first: FloatArray?, second: FloatArray?): Boolean = first.contentEquals(second)
fun doubleContentsInLibrary(first: DoubleArray?, second: DoubleArray?): Boolean = first.contentEquals(second)
inline fun inlineFloatContents(first: FloatArray?, second: FloatArray?, transform: (Boolean) -> Boolean): Boolean = transform(first.contentEquals(second))
inline fun inlineDoubleContents(first: DoubleArray?, second: DoubleArray?, transform: (Boolean) -> Boolean): Boolean = transform(first.contentEquals(second))
fun boundFloatContents(value: FloatArray?): (FloatArray?) -> Boolean = value::contentEquals
fun boundDoubleContents(value: DoubleArray?): (DoubleArray?) -> Boolean = value::contentEquals

class ArrayContentValue(private val number: Int) {
    override fun equals(other: Any?): Boolean = other is ArrayContentValue && number == other.number
    override fun hashCode(): Int = number * 13
    override fun toString(): String = "value:$number"
}

fun makeArrayContents(): Array<Any?> = arrayOf(arrayOf(ArrayContentValue(7), null), "tail")

fun importedArrayList(value: Array<*>): MutableList<Array<*>> {
    val values = mutableListOf<Array<*>>()
    values.add(value)
    return values
}
fun importedArrayContains(values: MutableList<Array<*>>, value: Array<*>): Boolean = value in values
fun importedArraySizes(values: List<Array<*>>): Int {
    var result = 0
    for (value in values) result += value.size
    return result
}

fun primitiveArrayContents(): Array<Any?> = arrayOf(
    byteArrayOf(-1, 2), shortArrayOf(-3, 4), intArrayOf(-5, 6), longArrayOf(-7L, 8L),
    floatArrayOf(1.5f, 2.5f), doubleArrayOf(3.5, 4.5), booleanArrayOf(true, false), charArrayOf('x', 'y'),
    ubyteArrayOf(255u, 2u), ushortArrayOf(65535u, 4u),
    uintArrayOf(UInt.MAX_VALUE, 6u), ulongArrayOf(ULong.MAX_VALUE, 8uL),
)
