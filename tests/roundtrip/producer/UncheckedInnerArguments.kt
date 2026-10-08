package roundtrip.innercast

import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class InnerCastOwner<T>(val value: T) {
    inner class Item(val count: Int)
    inner class GenericItem<U>(val payload: U, val count: Int) {
        fun sameCount(other: GenericItem<U>): Boolean = count == other.count
    }

    private fun consume(item: Item): Int = item.count
    fun ordinary(raw: Any): Int = consume(raw as InnerCastOwner<T>.Item)
    suspend fun suspending(raw: Any): Int {
        val item = raw as InnerCastOwner<T>.Item
        val count = consume(item)
        suspendCoroutine<Unit> { it.resume(Unit) }
        return count
    }
}

fun <T> consumeInner(item: InnerCastOwner<T>.Item): Int = item.count
fun <T, U> consumeGenericInner(item: InnerCastOwner<T>.GenericItem<U>): Int = item.count

class InnerCastHolder<T>(val item: InnerCastOwner<T>.Item)
fun <T> returnInner(item: InnerCastOwner<T>.Item): InnerCastOwner<T>.Item = item
