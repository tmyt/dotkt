package roundtrip.collectionarrayidentity

fun collectionArrayIdentity(values: Array<List<String>>): Array<List<String>> = values
fun collectionVarargSize(vararg values: List<String>): Int = values.size
class CollectionArrays(var values: Array<List<String>>)

open class ArrayElementBase(val value: Int)
class ArrayElementDerived : ArrayElementBase(23)
class ReadOnlyElements<T>(values: Collection<T>) : Collection<T> by values
class ReadOnlyListElements<T>(values: List<T>) : List<T> by values
class MutableElements<T>(private val values: MutableList<T>) : AbstractMutableCollection<T>() {
    override val size: Int get() = values.size
    override fun add(element: T): Boolean = values.add(element)
    override fun iterator(): MutableIterator<T> = values.iterator()
    override fun contains(element: T): Boolean = values.contains(element)
    fun contains(element: String): Boolean = element.isEmpty()
}
class MutableListElements<T>(private val values: MutableList<T>) : AbstractMutableList<T>() {
    override val size: Int get() = values.size
    override fun get(index: Int): T = values[index]
    override fun set(index: Int, element: T): T = values.set(index, element)
    override fun add(index: Int, element: T) { values.add(index, element) }
    override fun removeAt(index: Int): T = values.removeAt(index)
}

open class ArraySink<T> {
    protected fun accept(values: Array<T?>): Int = values.size
}

class ArraySinkChild<U, V> : ArraySink<V>() {
    fun acceptValues(values: Array<V?>): Int = accept(values)
}

fun <T> genericArrayIdentity(values: Array<T>): Array<T> = values
fun <T> collectionIteratorReference(): (Iterable<T>) -> Iterator<T> = Iterable<T>::iterator
inline fun <reified T> collectionCheckedCast(value: Any?): T = value as T
inline fun <reified T> collectionSafeCast(value: Any?): T? = value as? T
inline fun <reified T> collectionIsInstance(value: Any?): Boolean = value is T
inline fun <reified T> nullableCollectionIsInstance(value: Any?): Boolean = collectionIsInstance<T?>(value)
inline fun <reified T> capturedCollectionIsInstance(): (Any?) -> Boolean = { it is T }
fun readProjectedCollections(values: Array<out Collection<ArrayElementBase>>): Int =
    values[0].first().value

fun <T : Any> selectArrayOrMap(values: Array<T>, map: Map<String, T>, fromArray: Boolean): T =
    if (fromArray) values[0] else map["value"]!!

fun <K, V> importedMapKeys(map: Map<K, V>): Set<K> = map.keys
fun <T> importedListReplace(values: MutableList<T>, value: T): T = values.set(0, value)
fun <T> importedListRemove(values: MutableList<T>): T = values.removeAt(0)
fun <T> importedArrayListReplace(values: ArrayList<T>, value: T): T = values.set(0, value)
fun <T> importedHashMapReplace(values: HashMap<String, T>, value: T): T? = values.put("value", value)
fun <A, T> importedCollectionAppend(values: MutableCollection<T>, value: T, context: A): A {
    values.add(value)
    return context
}

fun <A, T> importedArraySort(values: Array<T>, comparator: Comparator<in T>, context: A): A {
    values.sortWith(comparator)
    return context
}
