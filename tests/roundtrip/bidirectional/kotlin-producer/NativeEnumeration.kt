import roundtrip.enumeration.ReferencedMutableEnumerationSet

private open class LocalMutableEnumerationSet<T>(private val storage: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = storage.size
    override fun iterator(): MutableIterator<T> = storage.iterator()
    override fun contains(element: T): Boolean = storage.contains(element)
    override fun containsAll(elements: Collection<T>): Boolean = storage.containsAll(elements)
    override fun add(element: T): Boolean = if (storage.contains(element)) false else storage.add(element)
}

private class LocalInheritedEnumerationSet : LocalMutableEnumerationSet<String>(mutableListOf("local"))

private class NamedEnumerationSet : LocalMutableEnumerationSet<String>(mutableListOf("named")) {
    fun GetEnumerator(): Int = 99
}

private class ReferencedInheritedEnumerationSet : ReferencedMutableEnumerationSet<String>(mutableListOf("referenced"))

fun stringMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf("a", "b"))
fun intMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf(1, 2))
fun nullableMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf<Int?>(null, 3))
fun localInheritedEnumerationSet(): Any = LocalInheritedEnumerationSet()
fun referencedInheritedEnumerationSet(): Any = ReferencedInheritedEnumerationSet()
fun namedEnumerationSet(): Any = NamedEnumerationSet()
fun authoredEnumeratorValue(value: Any): Int = (value as NamedEnumerationSet).GetEnumerator()

private fun <T> enumerateFromKotlin(values: Iterable<T>): List<T> {
    val result = mutableListOf<T>()
    for (value in values) result.add(value)
    return result
}

fun kotlinMutableEnumerationChecks(): Boolean {
    val strings: MutableSet<String> = LocalMutableEnumerationSet(mutableListOf("a", "b"))
    val ints: MutableSet<Int> = LocalMutableEnumerationSet(mutableListOf(1, 2))
    val nullable: MutableSet<Int?> = LocalMutableEnumerationSet(mutableListOf<Int?>(null, 3))
    val local: MutableSet<String> = LocalInheritedEnumerationSet()
    val referenced: MutableSet<String> = ReferencedInheritedEnumerationSet()
    return strings.contains("a") && ints.contains(1) && nullable.contains(null)
        && local.contains("local") && referenced.contains("referenced")
        && enumerateFromKotlin(strings) == listOf("a", "b")
        && enumerateFromKotlin(ints) == listOf(1, 2)
        && enumerateFromKotlin(nullable) == listOf<Int?>(null, 3)
        && enumerateFromKotlin(local) == listOf("local")
        && enumerateFromKotlin(referenced) == listOf("referenced")
}
