/* Kotlin-authored maps dispatch through their declaration-owned slots. Native dictionaries
 * select a CLR dictionary contract for each operation and retain it for a returned view; Kotlin
 * variance does not require that contract's invariant K/V arguments to match the source view.
 */
@file:Suppress("NOTHING_TO_INLINE", "UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.KotlinMapDefaultSlots
import DotKt.Runtime.CompilerServices.KotlinMutableMapSlots
import DotKt.Runtime.CompilerServices.projectedCollectionFamily
import DotKt.Runtime.CompilerServices.projectedViewEnumeratorErased
import DotKt.Runtime.CompilerServices.structuralEquals

// Native access operations retain Kotlin return conventions and covariance-safe boxed values.
internal fun <V> ClrNativeMapAccess.clrMapItem(key: Any?): V {
    @Suppress("UNCHECKED_CAST")
    return this.rawGet(key) as V
}

internal fun ClrNativeMapAccess.clrMapSetItem(key: Any?, value: Any?): Unit = this.rawSet(key, value)

internal fun ClrNativeMapAccess.clrMapRemoveKey(key: Any?): Boolean {
    val had = this.Contains(key)
    this.rawRemove(key)
    return had
}

internal fun <K> ClrNativeMapAccess.clrMapNativeKeys(): Iterable<K> {
    val e = this.GetEnumerator()
    val out = ArrayList<K>()
    @Suppress("UNCHECKED_CAST")
    try { while (e.MoveNext()) out.add(e.key() as K) } finally { e.close() }
    return out
}

// ---- Map reads ------------------------------------------------------------

public fun <K, V> clrMapToString(m: Any, preferredView: Any? = null): String {
    if (m is KotlinMapDefaultSlots) return m.toString()
    val e = ClrNativeMapAccess(m, preferredView).GetEnumerator()
    val sb = StringBuilder()
    sb.append("{")
    var first = true
    try {
        while (e.MoveNext()) {
            if (!first) sb.append(", ")
            first = false
            sb.append(clrElemToString(e.key()))
            sb.append("=")
            sb.append(clrElemToString(e.value()))
        }
    } finally { e.close() }
    sb.append("}")
    return sb.toString()
}

public fun <K, V> clrMapGet(m: Any, key: Any?, preferredView: Any? = null): V? {
    if (m is KotlinMapDefaultSlots) return m.dotktMapGet(key) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else null
}

public fun <K, V> clrMapIsEmpty(m: Any, preferredView: Any? = null): Boolean =
    if (m is KotlinMapDefaultSlots) m.dotktMapIsEmpty() else ClrNativeMapAccess(m, preferredView).count() == 0

public fun <K, V> clrMapSize(m: Any, preferredView: Any? = null): Int =
    if (m is KotlinMapDefaultSlots) m.dotktMapSize() else ClrNativeMapAccess(m, preferredView).count()

public fun <K, V> clrMapContainsKey(m: Any, key: Any?, preferredView: Any? = null): Boolean =
    if (m is KotlinMapDefaultSlots) m.dotktMapContainsKey(key) else ClrNativeMapAccess(m, preferredView).Contains(key)

public fun <K, V> clrMapContainsValue(m: Any, value: Any?, preferredView: Any? = null): Boolean {
    if (m is KotlinMapDefaultSlots) return m.dotktMapContainsValue(value)
    return ClrNativeMapAccess(m, preferredView).hasValue(value)
}

private fun ClrNativeMapAccess.hasValue(value: Any?): Boolean {
    val e = GetEnumerator()
    try { while (e.MoveNext()) if (e.value() == value) return true } finally { e.close() }
    return false
}

public fun <K, V> clrMapGetOrDefault(m: Any, key: Any?, defaultValue: V, preferredView: Any? = null): V {
    if (m is KotlinMapDefaultSlots) return m.dotktMapGetOrDefault(key, defaultValue) as V
    val d = ClrNativeMapAccess(m, preferredView)
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else defaultValue
}

public fun <K, V> clrMapKeys(m: Any, preferredView: Any? = null): Set<K> =
    if (m is KotlinMapDefaultSlots) m.dotktMapKeys() as Set<K> else ClrReadOnlyMapKeys(ClrNativeMapAccess(m, preferredView))

public fun <K, V> clrMapValues(m: Any, preferredView: Any? = null): Collection<V> =
    if (m is KotlinMapDefaultSlots) m.dotktMapValues() as Collection<V> else ClrReadOnlyMapValues(ClrNativeMapAccess(m, preferredView))

public fun <K, V> clrMapEntries(m: Any, preferredView: Any? = null): Set<Map.Entry<K, V>> =
    if (m is KotlinMapDefaultSlots) m.dotktMapEntries() as Set<Map.Entry<K, V>> else ClrReadOnlyMapEntries(ClrNativeMapAccess(m, preferredView))

// ---- MutableMap defaults ------------------------------------------------------------------------------------------

public fun <K, V> clrMapPut(m: Any, key: K, value: V, preferredView: Any? = null): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapPut(key, value) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    if (d.Contains(key)) {
        val old = d.clrMapItem<V>(key)
        d.clrMapSetItem(key, value)
        return old
    }
    d.clrMapSetItem(key, value)
    return null
}

public fun <K, V> clrMapRemove(m: Any, key: Any?, preferredView: Any? = null): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapRemove(key) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    if (d.Contains(key)) {
        val old = d.clrMapItem<V>(key)
        d.clrMapRemoveKey(key)
        return old
    }
    return null
}

public fun <K, V> clrMapRemoveKV(m: Any, key: Any?, value: Any?, preferredView: Any? = null): Boolean {
    if (m is KotlinMutableMapSlots) return m.dotktMapRemoveEntry(key, value)
    val d = ClrNativeMapAccess(m, preferredView)
    return if (d.Contains(key) && d.clrMapItem<V>(key) == value) { d.clrMapRemoveKey(key); true } else false
}

public fun <K, V> clrMapPutAll(m: Any, from: Any, preferredView: Any? = null, fromView: Any? = null): Unit {
    if (m is KotlinMutableMapSlots) { m.dotktMapPutAll(from); return }
    val d = ClrNativeMapAccess(m, preferredView)
    if (from is KotlinMapDefaultSlots) {
        val iterator = clrProjectedIterator<Map.Entry<*, *>>(from.dotktMapEntries())
        while (iterator.hasNext()) {
            val entry = iterator.next()
            d.clrMapSetItem(entry.key, entry.value)
        }
        return
    }
    val s = ClrNativeMapAccess(from, fromView)
    for (k in s.clrMapNativeKeys<K>()) d.clrMapSetItem(k, s.clrMapItem<V>(k))
}

public fun <K, V> clrMapPutIfAbsent(m: Any, key: K, value: V, preferredView: Any? = null): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapPutIfAbsent(key, value) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    return if (d.Contains(key)) d.clrMapItem<V>(key) else { d.clrMapSetItem(key, value); null }
}

public fun <K, V> clrMapReplace(m: Any, key: Any?, value: V, preferredView: Any? = null): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapReplace(key, value) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    if (!d.Contains(key)) return null
    val previous = d.clrMapItem<V>(key)
    d.clrMapSetItem(key, value)
    return previous
}

public fun <K, V> clrMapReplaceKVV(m: Any, key: Any?, oldValue: Any?, newValue: V, preferredView: Any? = null): Boolean {
    if (m is KotlinMutableMapSlots) return m.dotktMapReplaceEntry(key, oldValue, newValue)
    val d = ClrNativeMapAccess(m, preferredView)
    return if (d.Contains(key) && d.clrMapItem<V>(key) == oldValue) { d.clrMapSetItem(key, newValue); true } else false
}

/** A missing key inserts [value]; otherwise a null remap result removes the entry. */
public fun <K, V> clrMapMerge(m: Any, key: K, value: V, remappingFunction: (V, V) -> V?, preferredView: Any? = null): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapMerge(key, value, remappingFunction as (Any?, Any?) -> Any?) as V?
    val d = ClrNativeMapAccess(m, preferredView)
    if (!d.Contains(key)) {
        d.clrMapSetItem(key, value)
        return value
    }
    val computed = remappingFunction(d.clrMapItem<V>(key), value)
    if (computed == null) {
        d.clrMapRemoveKey(key)
        return null
    }
    d.clrMapSetItem(key, computed)
    return computed
}

/**
 * `MutableMap.entries: MutableSet<MutableEntry<K,V>>` — the slot lowers to ICollection<MutableEntry>, which the
 * snapshot ArrayList's BCL List satisfies at runtime (the `as` is a lowered castclass onto ICollection). Entries are
 * LIVE (value reads and setValue go through the backing map); the set itself is a snapshot.
 */
public fun <K, V> clrMapMutableEntries(m: Any, preferredView: Any? = null): MutableSet<MutableMap.MutableEntry<K, V>> {
    if (m is KotlinMutableMapSlots) return m.dotktMapMutableEntries() as MutableSet<MutableMap.MutableEntry<K, V>>
    val d = ClrNativeMapAccess(m, preferredView)
    val e = d.GetEnumerator()
    val out = LinkedHashSet<MutableMap.MutableEntry<K, V>>()
    @Suppress("UNCHECKED_CAST")
    try { while (e.MoveNext()) out.add(ClrMutableMapEntry(d, e.key() as K)) } finally { e.close() }
    return out
}

/**
 * Live `MutableMap.keys` view. The BCL Dictionary.KeyCollection is only an ICollection<K>, so it cannot carry the
 * independent MutableSet identity. This Kotlin view does, while forwarding remove/clear to the backing raw map.
 * Adding a key has no corresponding value and is therefore unsupported, matching Kotlin's MutableMap.keys contract.
 */
public fun <K, V> clrMapMutableKeys(m: Any, preferredView: Any? = null): MutableSet<K> =
    if (m is KotlinMutableMapSlots) m.dotktMapMutableKeys() as MutableSet<K> else ClrMutableMapKeySet(ClrNativeMapAccess(m, preferredView))

public fun <K, V> clrMapMutableValues(m: Any, preferredView: Any? = null): MutableCollection<V> =
    if (m is KotlinMutableMapSlots) m.dotktMapMutableValues() as MutableCollection<V>
    else ClrNativeMapValueCollection(ClrNativeMapAccess(m, preferredView))

public fun <K, V> clrMapClear(m: Any, preferredView: Any? = null) {
    if (m is KotlinMutableMapSlots) m.dotktMapClear() else ClrNativeMapAccess(m, preferredView).rawClear()
}

// ---- pure-Kotlin backing types --------------------------------------------------------------------------------------

/** A live entry keeps its selected dictionary access plan, including after a generic-only
 *  receiver crosses a Kotlin boundary. Key/value narrowing happens at the Kotlin view. */
private class ClrMutableMapEntry<K, V>(private val raw: ClrNativeMapAccess, override val key: K) :
    MutableMap.MutableEntry<K, V> {
    @Suppress("UNCHECKED_CAST")
    override val value: V get() = raw.rawGet(key) as V
    override fun setValue(newValue: V): V {
        @Suppress("UNCHECKED_CAST")
        val old = raw.rawGet(key) as V
        raw.rawSet(key, newValue)
        return old
    }
    override fun equals(other: Any?): Boolean = AbstractMap.entryEquals(this, other)
    override fun hashCode(): Int = AbstractMap.entryHashCode(this)
    override fun toString(): String = key.toString() + "=" + value.toString()
}

private class ClrReadOnlyMapIterator<E>(
    private val cursor: ClrNativeMapCursor, private val read: (ClrNativeMapCursor) -> E
) : Iterator<E>, AutoCloseable {
    private var buffered = false
    private var closed = false
    override fun close() {
        if (closed) return
        closed = true
        buffered = false
        cursor.close()
    }
    override fun hasNext(): Boolean {
        if (closed || buffered) return buffered
        try {
            buffered = cursor.MoveNext()
            if (!buffered) close()
            return buffered
        } catch (error: Throwable) { close(); throw error }
    }
    override fun next(): E {
        if (!hasNext()) throw NoSuchElementException()
        buffered = false
        try { return read(cursor) }
        catch (error: Throwable) { close(); throw error }
    }
}

// Read the argument's selected collection face without narrowing its boxed elements:
// covariantly widened views can be queried with values outside their native type.
private inline fun allMapViewCandidates(view: Any, elements: Any, accepts: (Any?) -> Boolean): Boolean {
    val preferred = projectedCollectionFamily(view, null)
    val cursor = (projectedViewEnumeratorErased(elements, preferred, 0)
        ?: (elements as ClrRawEnumerable).GetEnumerator()) as ClrRawEnumerator
    try {
        while (cursor.MoveNext()) if (!accepts(cursor.current())) return false
        return true
    } finally { if (cursor is AutoCloseable) cursor.close() }
}

private abstract class ClrReadOnlyMapSet<E>(protected val raw: ClrNativeMapAccess) : AbstractSet<E>() {
    override val size: Int get() = raw.count()
    protected abstract fun hasElement(element: Any?): Boolean
    override fun contains(element: E): Boolean = hasElement(element)
    override fun containsAll(elements: Collection<E>): Boolean = allMapViewCandidates(this, elements) { hasElement(it) }
    // Membership honors the native comparer. Kotlin Set equality, like its hash code,
    // compares the actual elements rather than treating two spellings as the same key.
    override fun equals(other: Any?): Boolean = structuralEquals(this, other, 1)
}

private class ClrReadOnlyMapKeys<K>(raw: ClrNativeMapAccess) : ClrReadOnlyMapSet<K>(raw) {
    override fun hasElement(element: Any?): Boolean = raw.Contains(element)
    override fun iterator(): Iterator<K> = ClrReadOnlyMapIterator(raw.GetEnumerator()) { it.key() as K }
}

private class ClrReadOnlyMapValues<V>(private val raw: ClrNativeMapAccess) : AbstractCollection<V>() {
    override val size: Int get() = raw.count()
    override fun iterator(): Iterator<V> = ClrReadOnlyMapIterator(raw.GetEnumerator()) { it.value() as V }
    override fun contains(element: V): Boolean = raw.hasValue(element)
    override fun containsAll(elements: Collection<V>): Boolean = allMapViewCandidates(this, elements) { raw.hasValue(it) }
}

private class ClrReadOnlyMapEntries<K, V>(raw: ClrNativeMapAccess) : ClrReadOnlyMapSet<Map.Entry<K, V>>(raw) {
    override fun hasElement(element: Any?): Boolean {
        if (element !is Map.Entry<*, *>) return false
        val key = element.key
        return raw.Contains(key) && raw.rawGet(key) == element.value
    }
    override fun iterator(): Iterator<Map.Entry<K, V>> =
        ClrReadOnlyMapIterator(raw.GetEnumerator()) { ClrReadOnlyMapEntry<K, V>(raw, it.key() as K) }
}

private class ClrReadOnlyMapEntry<K, V>(private val raw: ClrNativeMapAccess, override val key: K) : Map.Entry<K, V> {
    override val value: V get() = raw.rawGet(key) as V
    override fun equals(other: Any?): Boolean = AbstractMap.entryEquals(this, other)
    override fun hashCode(): Int = AbstractMap.entryHashCode(this)
    override fun toString(): String = key.toString() + "=" + value.toString()
}

private class ClrMapNativeValueIterator<V>(private val raw: ClrNativeMapAccess) : MutableIterator<V> {
    private val keys = raw.clrMapNativeKeys<Any?>().iterator()
    private var lastKey: Any? = null
    private var canRemove = false
    override fun hasNext(): Boolean = keys.hasNext()
    override fun next(): V {
        val key = keys.next()
        lastKey = key
        canRemove = true
        return raw.rawGet(key) as V
    }
    override fun remove() {
        if (!canRemove) throw IllegalStateException("next() has not been called")
        raw.rawRemove(lastKey)
        canRemove = false
    }
}

private class ClrNativeMapValueCollection<V>(private val raw: ClrNativeMapAccess) : MutableCollection<V> {
    override fun toString(): String = clrCollToString(this)
    override val size: Int get() = raw.count()
    override fun isEmpty(): Boolean = size == 0
    override fun iterator(): MutableIterator<V> = ClrMapNativeValueIterator(raw)
    override fun contains(element: V): Boolean {
        val cursor = raw.GetEnumerator()
        try { while (cursor.MoveNext()) if (cursor.value() == element) return true }
        finally { cursor.close() }
        return false
    }
    override fun containsAll(elements: Collection<V>): Boolean {
        for (element in elements) if (!contains(element)) return false
        return true
    }
    override fun add(element: V): Boolean = throw UnsupportedOperationException("add is not supported by MutableMap.values")
    override fun addAll(elements: Collection<V>): Boolean =
        if (elements.isEmpty()) false else throw UnsupportedOperationException("addAll is not supported by MutableMap.values")
    override fun remove(element: V): Boolean {
        val cursor = raw.GetEnumerator()
        try {
            while (cursor.MoveNext()) if (cursor.value() == element) {
                raw.rawRemove(cursor.key())
                return true
            }
        } finally { cursor.close() }
        return false
    }
    private fun removeMatching(elements: Collection<V>, retain: Boolean): Boolean {
        val remove = ArrayList<Any?>()
        val cursor = raw.GetEnumerator()
        try {
            while (cursor.MoveNext()) if (elements.contains(cursor.value() as V) != retain) remove.add(cursor.key())
        } finally { cursor.close() }
        for (key in remove) raw.rawRemove(key)
        return !remove.isEmpty()
    }
    override fun removeAll(elements: Collection<V>): Boolean = removeMatching(elements, false)
    override fun retainAll(elements: Collection<V>): Boolean = removeMatching(elements, true)
    override fun clear() = raw.rawClear()
}

private class ClrMutableMapKeySet<K>(private val raw: ClrNativeMapAccess) : MutableSet<K> {
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 1)
    override fun hashCode(): Int = DotKt.Runtime.CompilerServices.structuralHash(this, 1)

    override val size: Int get() = raw.count()
    override fun isEmpty(): Boolean = size == 0
    override fun contains(element: K): Boolean = raw.Contains(element)
    override fun containsAll(elements: Collection<K>): Boolean {
        for (element in elements) if (!raw.Contains(element)) return false
        return true
    }
    override fun iterator(): MutableIterator<K> {
        val snapshot = ArrayList<K>()
        val e = raw.GetEnumerator()
        @Suppress("UNCHECKED_CAST")
        try { while (e.MoveNext()) snapshot.add(e.key() as K) } finally { e.close() }
        return object : MutableIterator<K> {
            private var index = 0
            private var last: K? = null
            private var hasLast = false
            override fun hasNext(): Boolean = index < snapshot.size
            override fun next(): K {
                if (!hasNext()) throw NoSuchElementException()
                val value = snapshot[index]
                index++
                last = value
                hasLast = true
                return value
            }
            override fun remove() {
                if (!hasLast) throw IllegalStateException("next() has not been called")
                @Suppress("UNCHECKED_CAST")
                raw.rawRemove(last as K)
                hasLast = false
            }
        }
    }
    override fun add(element: K): Boolean = throw UnsupportedOperationException("add is not supported by MutableMap.keys")
    override fun addAll(elements: Collection<K>): Boolean =
        if (elements.isEmpty()) false else throw UnsupportedOperationException("addAll is not supported by MutableMap.keys")
    override fun remove(element: K): Boolean = raw.clrMapRemoveKey(element)
    override fun removeAll(elements: Collection<K>): Boolean {
        var changed = false
        for (element in elements) if (raw.clrMapRemoveKey(element)) changed = true
        return changed
    }
    override fun retainAll(elements: Collection<K>): Boolean {
        val remove = ArrayList<K>()
        val e = raw.GetEnumerator()
        @Suppress("UNCHECKED_CAST")
        try {
            while (e.MoveNext()) {
                val key = e.key() as K
                if (!elements.contains(key)) remove.add(key)
            }
        } finally { e.close() }
        var changed = false
        for (key in remove) if (raw.clrMapRemoveKey(key)) changed = true
        return changed
    }
    override fun clear() = raw.rawClear()
}
