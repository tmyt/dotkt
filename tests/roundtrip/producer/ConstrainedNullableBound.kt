// #345 producer half: the consumer imports this interface and its implementations through the emitted DLL/KLIB.
package roundtrip.constrainedbound

import System.Collections.Generic.List as ClrList

interface UsableBound { val name: String }
internal interface HiddenBound : UsableBound
open class VisibleBoundBase : HiddenBound { override val name get() = "base" }
fun <T : VisibleBoundBase> keepBound(value: T): T = value
open class ScopedBoundOwner {
    protected interface State
    open class Item : State
}

interface ReferencedBoundSink<T> { fun accept(x: T): String }

class ReferencedIntBoundSink : ReferencedBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}

class ReferencedAnyBoundSink : ReferencedBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}

class ResultSource {
    interface Mark
    open class Tagged<out K> : PendingItems.Item(), Mark
    class ResultItem<T>(val result: T) : Tagged<String>()
}

class ItemSlot<A : PendingItems.Item> {
    var item: A? = null
}

class RelatedItemSlot<B : A, A : PendingItems.Item> { var item: B? = null }

fun <A : PendingItems.Item?> keepItem(value: A?): A? = value

fun checkLocalMethodBounds(value: ResultSource.ResultItem<*>) {
    check(marked(value) === value)
    check(dependent<ResultSource.Tagged<*>, ResultSource.ResultItem<*>>(value, value) === value)
    check(nullableOperand(value, null) === value)
    check(boxed<ResultSource.ResultItem<*>>() == null)
}

fun localRelatedItem(value: ResultSource.ResultItem<*>): Boolean {
    val slot = RelatedItemSlot<ResultSource.ResultItem<*>, ResultSource.ResultItem<*>>()
    slot.item = value
    return slot.item === value
}
private fun <A> marked(value: A): A where A : PendingItems.Item, A : ResultSource.Mark = value
private fun <A : PendingItems.Item, B : A> dependent(first: A, second: B): A = second
private fun <A : PendingItems.Item> nullableOperand(value: A, xs: ClrList<A?>?): A = value
private fun <A : PendingItems.Item> boxed(): ItemSlot<A>? = null

class PendingItems<A : PendingItems.Item> {
    abstract class Item
    private val items = ArrayList<A>()
    fun put(item: A) { items.add(item) }
    fun get(index: Int): A = items[index]
    fun <T : Item> select(value: T): T = value
    fun <T : Item> projected(value: T, xs: ClrList<T?>?): T = value
}

fun localItemResult(): Long {
    val slot = ItemSlot<ResultSource.ResultItem<Long>>()
    slot.item = ResultSource.ResultItem(17L)
    val projected: ResultSource.ResultItem<*> = slot.item!!
    check(keepItem(projected) === projected)
    return slot.item!!.result
}

class StreamChunk<T>(val data: T) : System.IO.MemoryStream()
class ChunkList<A : System.IO.MemoryStream>(val items: ClrList<A>)
fun firstChunk(items: ClrList<StreamChunk<*>>): StreamChunk<*> = ChunkList<StreamChunk<*>>(items).items[0]
