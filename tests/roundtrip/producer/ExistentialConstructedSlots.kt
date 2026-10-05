import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class ExistentialOwnerHolder(val owner: ExistentialSlotOwner<String>)
class ExistentialItemHolder<T>(val item: ExistentialSlotOwner<T>.Item)

class ExistentialSlotOwner<T>(val value: T) {
    inner class Item(val count: Int)
    private var stored: Item? = null
    fun store(raw: Any) { stored = raw as ExistentialSlotOwner<T>.Item }
    fun storedItem(): Item? = stored
    private fun consume(item: Item): Int = item.count
    fun read(item: Item): Int = consume(item)
    fun same(item: Item, raw: Any): Boolean = item === raw
    fun nullable(item: Item?): Int = item?.count ?: 0
    fun fromAny(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        return consume(item)
    }
    fun unused(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        return 7
    }
    suspend fun suspended(raw: Any): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        val count = consume(item)
        suspendCoroutine<Unit> { it.resume(Unit) }
        return count
    }
    suspend fun deferred(raw: Any, pause: suspend () -> Unit): Int {
        val item = raw as ExistentialSlotOwner<T>.Item
        pause()
        return consume(item)
    }
}

class ExistentialHierarchyOwner<T> {
    open inner class Base(val count: Int)
    inner class Derived : Base(23)
    private fun consume(value: Base): Int = value.count
    fun fromAny(raw: Any): Int {
        val value = raw as ExistentialHierarchyOwner<T>.Derived
        return consume(value)
    }
}
