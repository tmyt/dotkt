@file:OptIn(ExperimentalUnsignedTypes::class)

package roundtrip.portablearraycontent

fun <T> arrayHash(values: Array<out T>?): Int = values.contentHashCode()
fun <T> arrayText(values: Array<out T>?): String = values.contentToString()
fun <T> arrayEquals(first: Array<out T>?, second: Array<out T>?): Boolean = first.contentEquals(second)
fun <T> arrayElement(values: Array<out T>, index: Int): T = values.elementAt(index)
inline fun <T> inlineArrayText(values: Array<out T>?): String = values.contentToString()
inline fun unsignedElement(values: UIntArray, index: Int): UInt = values.elementAt(index)
fun savedHash(values: IntArray): () -> Int = values::contentHashCode
fun savedText(values: Array<String?>): () -> String = values::contentToString

@Suppress("UNCHECKED_CAST")
fun <T> erasedNullHash(): Int = (arrayOfNulls<Any>(1) as Array<T>).contentHashCode()

@Suppress("UNCHECKED_CAST")
fun <T> erasedNullText(): String = (arrayOfNulls<Any>(1) as Array<T>).contentToString()
