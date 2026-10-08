package roundtrip.innercast

import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine
import kotlin.clr.ClrField

open class InnerCastBase(val seed: Int)
open class InnerCastMiddle(seed: Int) : InnerCastBase(seed)

class InnerCastOwner<T>(val value: T) {
    inner class Item(val count: Int)
    inner class DerivedItem(seed: Int) : InnerCastMiddle(seed)
    inner class GenericItem<U>(val payload: U, val count: Int) {
        fun sameCount(other: GenericItem<U>): Boolean = count == other.count
    }
    inner class FieldItem {
        lateinit var label: String
        @ClrField var count: Int = 0
    }
    inner class Middle<U>(val own: U) {
        inner class Leaf(val payload: T = value)
    }

    fun writeFields(item: FieldItem) { item.label = "written"; item.count = 19 }
    fun readFields(item: FieldItem): String = item.label + item.count
    fun throughBase(raw: Any): Int {
        val item = raw as InnerCastOwner<T>.DerivedItem
        var base: InnerCastBase = item
        base = item
        check(base === item)
        return consumeInnerBase(item)
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
fun consumeInnerBase(item: InnerCastBase): Int = item.seed
fun <T> returnInnerBase(item: InnerCastOwner<T>.DerivedItem): InnerCastBase = item
