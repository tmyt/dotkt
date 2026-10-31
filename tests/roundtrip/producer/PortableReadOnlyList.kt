package roundtrip.portablereadonlylist

fun <T> slice(source: List<T>, from: Int, to: Int): List<T> = source.subList(from, to)
fun <T> cursor(source: List<T>, index: Int): ListIterator<T> = source.listIterator(index)
