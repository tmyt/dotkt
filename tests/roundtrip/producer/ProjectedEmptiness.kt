package roundtrip.emptiness

fun importedListIsEmpty(value: Any): Boolean = (value as List<*>).isEmpty()
fun importedCollectionIsEmpty(value: Any): Boolean = (value as Collection<*>).isEmpty()
fun importedProjectedIsEmpty(value: List<*>): Boolean = value.isEmpty()

open class ImportedEmptyOverride : List<Int> by listOf(7, 9) {
    var calls = 0
    override fun isEmpty(): Boolean { calls++; return true }
}
