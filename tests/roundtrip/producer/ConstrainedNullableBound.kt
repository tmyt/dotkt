// #345 producer half: the consumer imports this interface and its implementations through the emitted DLL/KLIB.
package roundtrip.constrainedbound

interface ReferencedBoundSink<T> { fun accept(x: T): String }

class ReferencedIntBoundSink : ReferencedBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}

class ReferencedAnyBoundSink : ReferencedBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}

abstract class PendingItem
class ResultItem<T>(val result: T) : PendingItem()

class ItemSlot<A : PendingItem> {
    var item: A? = null
}

class PendingItems<A : PendingItem> {
    private val items = ArrayList<A>()
    fun put(item: A) { items.add(item) }
    fun get(index: Int): A = items[index]
}

fun localItemResult(): Long {
    val slot = ItemSlot<ResultItem<Long>>()
    slot.item = ResultItem(17L)
    return slot.item!!.result
}
