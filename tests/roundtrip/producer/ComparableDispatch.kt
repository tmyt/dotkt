package roundtrip.comparabledispatch

enum class ComparisonOrder { FIRST, SECOND }

class ComparisonBoundBox<T>(val value: T)
fun <T : Comparable<T>, U : ComparisonBoundBox<Int?>> keepMixedBounds(value: T, box: U): U = box

fun <T : Comparable<T>> compareImported(left: T, right: T): Int = left.compareTo(right)

inline fun <T : Comparable<T>> compareInlineImported(left: T, right: T): Int = left.compareTo(right)

fun <T : Comparable<T>> compareReferenceImported(left: T, right: T): Int {
    val operation = left::compareTo
    return operation(right)
}

fun maximumOrder(): ComparisonOrder = maxOf(ComparisonOrder.FIRST, ComparisonOrder.SECOND)

fun <T : Enum<T>> compareEnumBound(left: T, right: T): Int = left.compareTo(right)

fun <Tag, T : Enum<T>> compareEnumBoundShifted(tag: Tag, left: T, right: T): Int = left.compareTo(right)

inline fun <T : Enum<T>> compareEnumBoundInline(left: T, right: T): Int = left.compareTo(right)

fun <T : Enum<T>> compareEnumBoundReference(left: T, right: T): Int {
    val operation = left::compareTo
    return operation(right)
}

fun <T : Enum<T>> compareEnumBoundUnbound(left: T, right: T): Int {
    val operation = Enum<T>::compareTo
    return operation(left, right)
}

class EnumBoundComparisonBox<T : Enum<T>>(val value: T) {
    fun compare(other: T): Int = value.compareTo(other)
}

class ComparisonValue(val rank: Int) : Comparable<ComparisonValue> {
    override fun compareTo(other: ComparisonValue): Int = rank - other.rank
}
