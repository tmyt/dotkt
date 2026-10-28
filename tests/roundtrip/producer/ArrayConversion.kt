package roundtrip.arrayconversion

class LibraryArrayCollection<T>(private val values: List<T>) : AbstractCollection<T>() {
    var iteratorCalls = 0
    override val size: Int get() = values.size
    override fun iterator(): Iterator<T> { iteratorCalls++; return values.iterator() }
    fun objects(): Array<Any?> = toArray()
    fun <R> into(destination: Array<R>): Array<R> = toArray(destination)
}

inline fun <reified T> importedOrEmpty(value: Array<out T>?): Array<out T> = value.orEmpty()
inline fun <reified T> importedTypedArray(value: Collection<T>): Array<T> = value.toTypedArray()
