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

class ComparisonValue(val rank: Int) : Comparable<ComparisonValue> {
    override fun compareTo(other: ComparisonValue): Int = rank - other.rank
}
