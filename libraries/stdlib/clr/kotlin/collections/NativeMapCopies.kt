@file:Suppress("UNCHECKED_CAST")

package kotlin.collections

/**
 * Returns an independent shallow CLR dictionary copy using Kotlin key equality and hashing.
 * Changes to either dictionary are not reflected in the other. Element objects are not cloned.
 * A null key causes [IllegalArgumentException]; keys are never filtered and no partial copy is returned.
 * Existing native generic limitations still apply: `Int?` values use object storage, not CLR `Nullable<Int32>`.
 */
public fun <K : Any, V> Map<out K?, V>.toClrDictionary(): System.Collections.Generic.Dictionary<K, V> {
    val comparer = (DotKt.Runtime.CompilerServices.kotlinHashComparer<K>() as Any) as System.Collections.Generic.IEqualityComparer<K>
    val copy = System.Collections.Generic.Dictionary<K, V>(size, comparer)
    for ((key, value) in this) {
        require(key != null) { "A CLR Dictionary cannot contain a null key" }
        copy.Add(key, value)
    }
    return copy
}

/**
 * Returns an independent shallow CLR ordered dictionary copy in this map's iteration order.
 * Uses Kotlin key equality and hashing, shares element objects but not mutations, and rejects null keys.
 * Existing native generic limitations still apply: `Int?` values use object storage, not CLR `Nullable<Int32>`.
 */
public fun <K : Any, V> Map<out K?, V>.toClrOrderedDictionary(): System.Collections.Generic.OrderedDictionary<K, V> {
    val comparer = (DotKt.Runtime.CompilerServices.kotlinHashComparer<K>() as Any) as System.Collections.Generic.IEqualityComparer<K>
    val copy = System.Collections.Generic.OrderedDictionary<K, V>(size, comparer)
    for ((key, value) in this) {
        require(key != null) { "A CLR OrderedDictionary cannot contain a null key" }
        copy.Add(key, value)
    }
    return copy
}
