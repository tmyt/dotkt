@file:Suppress("UNCHECKED_CAST")

package roundtrip.sequenceviews

fun <T : Any> nonNull(source: Sequence<T?>): Sequence<T> = source.filterNotNull()

fun integers(source: Sequence<*>): Sequence<Int> = source.filterIsInstance<Int>()

inline fun <reified T> selected(source: Sequence<*>): Sequence<T> =
    source.filter { it is T } as Sequence<T>

fun <S, T> view(source: Sequence<S>): Sequence<T> = source as Sequence<T>
