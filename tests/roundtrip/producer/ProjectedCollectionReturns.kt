package roundtrip.projectedcollectionreturns

fun <T> collectionReturnIdentity(value: T): T = value
fun collectionReturnWiden(items: List<Any>): List<Any> = items
fun collectionReturnConsume(items: List<Any>): Int = items.size
class CollectionReturnBox<T>(var value: T)
fun collectionReturnFromBox(box: CollectionReturnBox<List<Comparable<*>>>): List<Any> = box.value
fun collectionReturnNative(items: System.Collections.Generic.IReadOnlyList<Int>):
    System.Collections.Generic.IReadOnlyList<Int> = items
