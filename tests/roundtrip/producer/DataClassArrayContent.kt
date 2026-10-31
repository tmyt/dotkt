package roundtrip.arraycontent

data class ArrayRecord<T>(val values: Array<T>, val ints: IntArray?)

fun <T> makeArrayRecord(values: Array<T>): ArrayRecord<T> = ArrayRecord(values, intArrayOf(1, 2))
