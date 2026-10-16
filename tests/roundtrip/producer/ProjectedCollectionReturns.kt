package roundtrip.projectedcollectionreturns

fun <T> collectionReturnIdentity(value: T): T = value
fun collectionReturnWiden(items: List<Any>): List<Any> = items
fun collectionReturnConsume(items: List<Any>): Int = items.size
class CollectionReturnBox<T>(var value: T)
fun collectionReturnFromBox(box: CollectionReturnBox<List<Comparable<*>>>): List<Any> = box.value
fun collectionReturnNative(items: System.Collections.Generic.IReadOnlyList<Int>):
    System.Collections.Generic.IReadOnlyList<Int> = items

interface CollectionReturnSink<T> {
    fun pick(items: List<T>): String
    fun pick(value: Any): String
}
interface CollectionReturnOtherSink<T> {
    fun pick(items: List<T>): String
    fun pick(value: Any): String
}
open class CollectionReturnBase<T> {
    open fun pick(items: List<T>): String = "base-list"
    open fun pick(value: Any): String = "base-any"
}
class CollectionReturnSinkImpl : CollectionReturnBase<Int>(), CollectionReturnSink<Int>, CollectionReturnOtherSink<Int> {
    override fun pick(items: List<Int>): String = "list"
    override fun pick(value: Any): String = "any"
}
class CollectionEnumeratorReturn<T>(private val source: ClrEnumerable<T>) {
    fun enumerator(): ClrEnumerator<T> = source.GetEnumerator()
}
open class CollectionConstructorReturn<T> {
    val result: String
    constructor(items: List<T>) { result = "list:" + items.size }
    constructor(value: Any) { result = "any:" + value }
}
