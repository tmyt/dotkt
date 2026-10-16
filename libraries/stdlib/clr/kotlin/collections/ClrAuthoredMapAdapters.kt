@file:Suppress("UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.KotlinMapDefaultSlots
import DotKt.Runtime.CompilerServices.KotlinMutableMapSlots

private fun mutableMapSlots(source: Any): KotlinMutableMapSlots =
    source as? KotlinMutableMapSlots ?: throw UnsupportedOperationException("Read-only map")

public fun clrAuthoredMapSet(source: Any, key: Any?, value: Any?) {
    mutableMapSlots(source).dotktMapPut(key, value)
}

public fun clrAuthoredMapAdd(source: Any, key: Any?, value: Any?) {
    val slots = mutableMapSlots(source)
    if ((source as KotlinMapDefaultSlots).dotktMapContainsKey(key)) throw IllegalArgumentException("Duplicate map key")
    slots.dotktMapPut(key, value)
}

public fun clrAuthoredMapRemove(source: Any, key: Any?): Boolean {
    val slots = mutableMapSlots(source)
    if (!(source as KotlinMapDefaultSlots).dotktMapContainsKey(key)) return false
    slots.dotktMapRemove(key)
    return true
}

public fun clrAuthoredMapClear(source: Any) { mutableMapSlots(source).dotktMapClear() }

public fun clrAuthoredMapCheckMutable(source: Any) { mutableMapSlots(source) }

/** A native dictionary exposes read-only ICollection views, independently of Kotlin's mutable view contract. */
private class ClrAuthoredMapCollection<T>(private val source: Any) : AbstractCollection<T>() {
    override val size: Int get() = DotKt.Runtime.CompilerServices.projectedCollectionCountErased(source)
    override fun contains(element: T): Boolean = clrCollContains(source as Collection<Any?>, element)
    override fun iterator(): Iterator<T> = clrProjectedIterator(source)
}

/** Only native dictionary boundaries use this checked, live view; Kotlin getters return their original view. */
public fun <T> clrAuthoredMapCollection(source: Any): Any = ClrAuthoredMapCollection<T>(source)

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.KeyValuePair")
internal class ClrAuthoredMapPair<K, V>(key: K, value: V)

private class ClrAuthoredMapIterator<K, V>(source: Any) : Iterator<ClrAuthoredMapPair<K, V>> {
    private val iterator = clrProjectedIterator<Map.Entry<*, *>>(source)
    override fun hasNext(): Boolean = iterator.hasNext()
    override fun next(): ClrAuthoredMapPair<K, V> {
        val entry = iterator.next()
        return ClrAuthoredMapPair(entry.key as K, entry.value as V)
    }
}

/** Source entries remain Kotlin entries; native enumeration materializes the CLR's value pair at that boundary. */
public fun <K, V> clrAuthoredMapIterator(source: Any): Any = ClrAuthoredMapIterator<K, V>(source)
