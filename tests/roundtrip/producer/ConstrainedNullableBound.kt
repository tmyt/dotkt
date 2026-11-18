// #345 producer half: the consumer imports this interface and its implementations through the emitted DLL/KLIB.
package roundtrip.constrainedbound

import System.Collections.Generic.List as ClrList

interface ReferencedBoundSink<T> { fun accept(x: T): String }

class ReferencedIntBoundSink : ReferencedBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}

class ReferencedAnyBoundSink : ReferencedBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}

class ResultSource {
    class ResultItem<T>(val result: T) : PendingItems.Item()
}

class ItemSlot<A : PendingItems.Item> {
    var item: A? = null
}

class PendingItems<A : PendingItems.Item> {
    abstract class Item
    private val items = ArrayList<A>()
    fun put(item: A) { items.add(item) }
    fun get(index: Int): A = items[index]
}

fun localItemResult(): Long {
    val slot = ItemSlot<ResultSource.ResultItem<Long>>()
    slot.item = ResultSource.ResultItem(17L)
    return slot.item!!.result
}

class StreamChunk<T>(val data: T) : System.IO.MemoryStream()
class ChunkList<A : System.IO.MemoryStream>(val items: ClrList<A>)
fun firstChunk(items: ClrList<StreamChunk<*>>): StreamChunk<*> = ChunkList<StreamChunk<*>>(items).items[0]
