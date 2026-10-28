package roundtrip.sortedbodies

fun <T : Comparable<T>> natural(values: Iterable<T>): List<T> = values.sorted()

fun <T> ordered(values: Iterable<T>, comparator: Comparator<in T>): List<T> = values.sortedWith(comparator)

inline fun <T> orderedInline(values: Iterable<T>, crossinline compare: (T, T) -> Int): List<T> =
    values.sortedWith(Comparator { a, b -> compare(a, b) })
