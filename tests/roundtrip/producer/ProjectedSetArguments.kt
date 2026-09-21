package roundtrip.setarguments

fun importedSetSize(value: Set<Any?>): Int = value.size
fun importedSetView(value: Set<Any?>): Set<Any?> = value
fun forwardImportedSet(value: Set<*>): Set<Any?> = importedSetView(value)
fun importedNullableSetSize(value: Set<Any?>?): Int = value?.size ?: -1
fun forwardImportedNullableSet(value: Set<*>?): Int = importedNullableSetSize(value)

private class SingleSetIterator : Iterator<Long> {
    private var available: Boolean = true
    override fun hasNext(): Boolean = available
    override fun next(): Long {
        if (!available) throw NoSuchElementException()
        available = false
        return 7L
    }
}

open class AuthoredSetBase : AbstractSet<Long>() {
    override val size: Int get() = 1
    override fun iterator(): Iterator<Long> = SingleSetIterator()
}
