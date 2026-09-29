import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class ExistentialSlotOwner<T>(val value: T) {
    inner class Item(val count: Int)
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
}
