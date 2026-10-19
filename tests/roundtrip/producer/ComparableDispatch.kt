package roundtrip.comparabledispatch

import kotlin.clr.ClrEnum

enum class ComparisonOrder { FIRST, SECOND }

@ClrEnum
enum class ExplicitComparisonOrder(value: Int) { FIRST(4), SECOND(-2) }

enum class PlainRichComparisonOrder(val label: String) { FIRST("first"), SECOND("second") }

enum class EntryRichComparisonOrder {
    FIRST { override fun marker(): Int = 1 }, SECOND;
    open fun marker(): Int = 2
}

fun compareRichBoundReference(left: EntryRichComparisonOrder, right: EntryRichComparisonOrder): Int {
    val operation = left::compareTo
    return operation(right)
}

fun compareRichUnboundReference(left: EntryRichComparisonOrder, right: EntryRichComparisonOrder): Int {
    val operation = EntryRichComparisonOrder::compareTo
    return operation(left, right)
}

interface ComparisonMarker<Tag>
enum class RichComparisonOrder(val label: String) : ComparisonMarker<String>, System.IComparable {
    FIRST("first"), SECOND("second");

    override fun CompareTo(other: Any?): Int = 77
}

class ComparisonBoundBox<T>(val value: T)
fun enumView(value: Any?): Enum<*>? = value as? Enum<*>
fun requireEnumView(value: Any): Enum<*> = value as Enum<*>
inline fun <reified T> hasEnumView(value: Any?): Boolean = value is T

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

inline fun <T : Enum<T>> compareEnumBoundSpliced(left: T, right: T, result: (Int) -> Int): Int =
    result(left.compareTo(right))

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

class DependentEnumComparisonBox<Tag, T> where T : Enum<T>, T : ComparisonMarker<Tag> {
    fun compareBound(left: T, right: T): Int {
        val operation = left::compareTo
        return operation(right)
    }
}

class ComparisonValue(val rank: Int) : Comparable<ComparisonValue> {
    override fun compareTo(other: ComparisonValue): Int = rank - other.rank
}
