package roundtrip.emptiness

fun importedListIsEmpty(value: Any): Boolean = (value as List<*>).isEmpty()
fun importedCollectionIsEmpty(value: Any): Boolean = (value as Collection<*>).isEmpty()
fun importedProjectedIsEmpty(value: List<*>): Boolean = value.isEmpty()
fun importedSetCount(value: Set<*>): Int = value.size
fun importedSetIsEmpty(value: Set<*>): Boolean = value.isEmpty()
fun importedListRange(value: List<*>): List<*> = value.subList(0, value.size)
fun importedCollectionCount(value: Collection<*>): Int = value.size
fun importedListUpcastCount(value: List<*>): Int {
    val collection: Collection<*> = value
    return collection.size
}

open class ImportedEmptyOverride : List<Int> by listOf(7, 9) {
    var calls = 0
    override fun isEmpty(): Boolean { calls++; return true }
}
