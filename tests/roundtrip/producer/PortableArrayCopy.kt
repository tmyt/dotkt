package roundtrip.arraycopy

fun <T> copyInLibrary(values: Array<T>): Array<T> = values.copyOf()
fun <T> resizeInLibrary(values: Array<T>, size: Int): Array<T?> = values.copyOf(size)
fun <T> rangeInLibrary(values: Array<T>, from: Int, to: Int): Array<T> = values.copyOfRange(from, to)
fun <T> appendInLibrary(values: Array<T>, tail: Array<out T>): Array<T> = values + tail
fun primitiveCopiesInLibrary(values: IntArray): IntArray = values.copyOfRange(1, values.size) + listOf(7)
fun boxedCopyInLibrary(values: IntArray): Array<Int> = values.toTypedArray()
