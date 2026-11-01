package roundtrip.readonlycursors

fun <T> cursorInLibrary(source: List<T>, index: Int): ListIterator<T> = source.listIterator(index)
