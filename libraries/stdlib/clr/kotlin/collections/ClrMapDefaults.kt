/*
 * Kotlin Map dispatch is independent of the native dictionary's signatures. An opaque receiver preserves
 * Kotlin covariance without an invariant IDictionary<K,V> cast at the helper boundary. Authored maps dispatch
 * through their exact compiler-provided capabilities, preserving overrides and original views. BCL-backed
 * Dictionary values retain the non-generic fallback and its existing snapshot read views/live mutable keys.
 * The native IDictionary facade is synthesized separately by bir2cir: missing-key indexers throw, mutation
 * slots use CLR return conventions, and enumeration converts Kotlin entries to real KeyValuePair values.
 */
@file:Suppress("NOTHING_TO_INLINE", "UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.KotlinMapDefaultSlots
import DotKt.Runtime.CompilerServices.KotlinMutableMapSlots

// ---- raw BCL member accessors: extensions on the NON-GENERIC System.Collections.IDictionary facade ----------------
//
// The put-read/write mirror of the read-side clrMapGet/clrMapContainsKey below. Their receiver is the NON-GENERIC
// `ClrRawDictionary` (the public map helpers cast their `Any` param to it once), so they are erasure-proof: the
// non-generic base interface is implemented by EVERY `Dictionary<K,V>` regardless of K/V, so a `groupingBy`/`mapValues`-
// built map (a `Dictionary<K, List<V>>` typed `IDictionary<K, IList<V>>`, INVARIANT in V) never hits a value-type-
// invariance EntryPointNotFound. Keys/values box through the `Any?`-typed members (unbox.any on narrowing).

/** Covariance-safe get_Item. The non-generic IDictionary indexer is null-on-missing (NOT throwing like the generic
 *  IDictionary<K,V> get_Item), so callers MUST pre-guard with Contains (all in-file callers do); the Kotlin-facing
 *  null-on-missing wrapper is [clrMapGet]. */
internal fun <V> ClrRawDictionary.clrMapItem(key: Any?): V {
    @Suppress("UNCHECKED_CAST")
    return this.rawGet(key) as V   // narrows directly into the object-erased return (no V? local)
}

/** Covariance-safe set_Item (void; the previous-value-returning wrapper is [clrMapPut]). */
internal fun ClrRawDictionary.clrMapSetItem(key: Any?, value: Any?): Unit = this.rawSet(key, value)

/** Covariance-safe Remove; returns whether the key was present (non-generic IDictionary.Remove is void, so test first). */
internal fun ClrRawDictionary.clrMapRemoveKey(key: Any?): Boolean {
    val had = this.Contains(key)
    this.rawRemove(key)
    return had
}

/** Covariance-safe key snapshot: iterate the NON-GENERIC enumerator's keys (mirrors [clrMapKeys] but returns a bare
 *  List for internal putAll / structEquals iteration). Keys narrow with `as K` (unbox.any). */
internal fun <K> ClrRawDictionary.clrMapNativeKeys(): Iterable<K> {
    val e = this.GetEnumerator()
    val out = ArrayList<K>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(e.key() as K)
    return out
}

// ---- Map (read) defaults --------------------------------------------------------------------------------------------
//
// WHY non-generic: `groupBy` returns `Map<K, List<V>>` (aliased `IDictionary<K, IReadOnlyList<V>>`) but the runtime
// object it builds is a `Dictionary<K, MutableList<V>>` (`IDictionary<K, IList<V>>`). CLR `IDictionary<,>` is INVARIANT
// in the value, so the concrete runtime map is NOT a verifiable `IDictionary<K, IReadOnlyList<V>>` — reading it through
// the NON-GENERIC `ClrRawDictionary` (System.Collections.IDictionary — implemented by EVERY `Dictionary<K,V>`) decouples
// the read path from the generic value-type invariance. The MUTABLE helpers route through the SAME facade for the same
// reason (`groupingBy`+`eachCount` builds a `Dictionary<K,Int>` whose put is value-type erased).

// Kotlin's Map.toString is `{a=1, b=2}` (AbstractMap.toString). The substituted BCL Dictionary renders its default
// `System.Collections.Generic.Dictionary`2[...]` instead, so the backend routes `map.toString()` / `println(map)` here
// (the map mirror of clrCollToString). Emitted into the rt assembly for kotc/bir2cir to target.
public fun <K, V> clrMapToString(m: Any): String {
    if (m is KotlinMapDefaultSlots) return m.toString()
    val e = (m as ClrRawDictionary).GetEnumerator()
    val sb = StringBuilder()
    sb.append("{")
    var first = true
    while (e.MoveNext()) {
        if (!first) sb.append(", ")
        first = false
        sb.append(clrElemToString(e.key()))            // recurse: nested collection/map keys/values render Kotlin-style (N7)
        sb.append("=")
        sb.append(clrElemToString(e.value()))
    }
    sb.append("}")
    return sb.toString()
}

public fun <K, V> clrMapGet(m: Any, key: K): V? {
    if (m is KotlinMapDefaultSlots) return m.dotktMapGet(key) as V?
    val d = m as ClrRawDictionary
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else null   // null flows DIRECTLY into the object-erased return (V?-NOTE)
}

public fun <K, V> clrMapIsEmpty(m: Any): Boolean =
    if (m is KotlinMapDefaultSlots) m.dotktMapIsEmpty() else (m as ClrRawCollection).count() == 0

// COVARIANCE-SAFE size / containsKey: `size` and `containsKey` on the Map interface are UNBOUND (no @ClrIntrinsic), and
// bir2cir Rule 5m routes `get_size`/`containsKey` on a Map/MutableMap owner to THESE helpers (exactly as `get`/`get_keys`/
// `get_values` route). They read through the NON-GENERIC facade (ICollection.Count / IDictionary.Contains) so they survive
// a groupBy-style value-type mismatch, AND make stdlib algorithms that pre-size via `this.size` (mapValues'
// `mapCapacity(size)`) covariance-safe transitively.
public fun <K, V> clrMapSize(m: Any): Int =
    if (m is KotlinMapDefaultSlots) m.dotktMapSize() else (m as ClrRawCollection).count()

public fun <K, V> clrMapContainsKey(m: Any, key: K): Boolean =
    if (m is KotlinMapDefaultSlots) m.dotktMapContainsKey(key) else (m as ClrRawDictionary).Contains(key)

public fun <K, V> clrMapContainsValue(m: Any, value: V): Boolean {
    if (m is KotlinMapDefaultSlots) return m.dotktMapContainsValue(value)
    val e = (m as ClrRawDictionary).GetEnumerator()
    while (e.MoveNext()) if (e.value() == value) return true
    return false
}

public fun <K, V> clrMapGetOrDefault(m: Any, key: K, defaultValue: V): V {
    if (m is KotlinMapDefaultSlots) return m.dotktMapGetOrDefault(key, defaultValue) as V
    val d = m as ClrRawDictionary
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else defaultValue
}

/** `Map.keys: Set<K>` — snapshot into an identity-bearing Kotlin Set rather than exposing Dictionary.KeyCollection. */
public fun <K, V> clrMapKeys(m: Any): Set<K> {
    if (m is KotlinMapDefaultSlots) return m.dotktMapKeys() as Set<K>
    val e = (m as ClrRawDictionary).GetEnumerator()
    val out = ArrayList<K>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(e.key() as K)
    return ClrMapSnapshotSet(out)
}

/** `Map.values: Collection<V>` — an ArrayList (BCL List) IS a Collection (IReadOnlyCollection) — snapshot. */
public fun <K, V> clrMapValues(m: Any): Collection<V> {
    if (m is KotlinMapDefaultSlots) return m.dotktMapValues() as Collection<V>
    val e = (m as ClrRawDictionary).GetEnumerator()
    val out = ArrayList<V>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(e.value() as V)
    return out
}

/** `Map.entries: Set<Map.Entry<K,V>>` — a pure-Set snapshot of LIVE entries (value reads go through the map). The
 *  element impl is ClrMutableMapEntry (implements MutableEntry TOO), so an entry surfaced through either the Map or
 *  the MutableMap view destructures/casts fine — at runtime every aliased map IS an IDictionary. The entry KEYS are
 *  snapshotted off the non-generic enumerator (covariance-safe); each entry's value read also goes non-generic. */
public fun <K, V> clrMapEntries(m: Any): Set<Map.Entry<K, V>> {
    if (m is KotlinMapDefaultSlots) return m.dotktMapEntries() as Set<Map.Entry<K, V>>
    val d = m as ClrRawDictionary
    val e = d.GetEnumerator()
    val out = ArrayList<Map.Entry<K, V>>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(ClrMutableMapEntry(d, e.key() as K))
    return ClrMapSnapshotSet(out)
}

// ---- MutableMap defaults ------------------------------------------------------------------------------------------

// NOTE (all V?-returning wrappers): never hold a `V?` in a LOCAL — a nullable unconstrained-generic local erases to
// a bare `gp:V` slot, and a null flowing into it `unbox.any`s to NRE when V is instantiated with a value type (the
// RC2 object-erasure covers the RETURN boundary, not locals). Structure the bodies so null only ever flows directly
// into the (object-erased) return.

public fun <K, V> clrMapPut(m: Any, key: K, value: V): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapPut(key, value) as V?
    val d = m as ClrRawDictionary
    if (d.Contains(key)) {
        val old = d.clrMapItem<V>(key)
        d.clrMapSetItem(key, value)
        return old
    }
    d.clrMapSetItem(key, value)
    return null
}

public fun <K, V> clrMapRemove(m: Any, key: K): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapRemove(key) as V?
    val d = m as ClrRawDictionary
    if (d.Contains(key)) {
        val old = d.clrMapItem<V>(key)
        d.clrMapRemoveKey(key)
        return old
    }
    return null
}

public fun <K, V> clrMapRemoveKV(m: Any, key: K, value: V): Boolean {
    if (m is KotlinMutableMapSlots) return m.dotktMapRemoveEntry(key, value)
    val d = m as ClrRawDictionary
    return if (d.Contains(key) && d.clrMapItem<V>(key) == value) { d.clrMapRemoveKey(key); true } else false
}

public fun <K, V> clrMapPutAll(m: Any, from: Any): Unit {
    if (m is KotlinMutableMapSlots) { m.dotktMapPutAll(from); return }
    val d = m as ClrRawDictionary
    if (from is KotlinMapDefaultSlots) {
        val iterator = clrProjectedIterator<Map.Entry<*, *>>(from.dotktMapEntries())
        while (iterator.hasNext()) {
            val entry = iterator.next()
            d.clrMapSetItem(entry.key, entry.value)
        }
        return
    }
    val s = from as ClrRawDictionary
    for (k in s.clrMapNativeKeys<K>()) d.clrMapSetItem(k, s.clrMapItem<V>(k))
}

public fun <K, V> clrMapPutIfAbsent(m: Any, key: K, value: V): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapPutIfAbsent(key, value) as V?
    val d = m as ClrRawDictionary
    return if (d.Contains(key)) d.clrMapItem<V>(key) else { d.clrMapSetItem(key, value); null }
}

public fun <K, V> clrMapReplace(m: Any, key: K, value: V): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapReplace(key, value) as V?
    val d = m as ClrRawDictionary
    return if (d.Contains(key)) clrMapPut<K, V>(m, key, value) else null
}

public fun <K, V> clrMapReplaceKVV(m: Any, key: K, oldValue: V, newValue: V): Boolean {
    if (m is KotlinMutableMapSlots) return m.dotktMapReplaceEntry(key, oldValue, newValue)
    val d = m as ClrRawDictionary
    return if (d.Contains(key) && d.clrMapItem<V>(key) == oldValue) { d.clrMapSetItem(key, newValue); true } else false
}

/**
 * `MutableMap.merge(key, value, remappingFunction)` (C2 — the java.util.Map.merge equivalent): absent key -> insert
 * [value]; present -> [remappingFunction](old, value); a null result removes the entry. Structured like clrMapPutIfAbsent
 * so null only ever flows DIRECTLY into the (object-erased) return — never into a `gp:V` local (see the V?-return NOTE).
 */
public fun <K, V> clrMapMerge(m: Any, key: K, value: V, remappingFunction: (V, V) -> V?): V? {
    if (m is KotlinMutableMapSlots) return m.dotktMapMerge(key, value, remappingFunction as (Any?, Any?) -> Any?) as V?
    val d = m as ClrRawDictionary
    if (!d.Contains(key)) {
        d.clrMapSetItem(key, value)
        return value
    }
    val computed = remappingFunction(d.clrMapItem<V>(key), value)
    if (computed == null) {
        d.clrMapRemoveKey(key)
        return null
    }
    // `computed` is smart-cast to V, but its CIR slot is the object-erased func return (`nullable:gp:V`). A plain store
    // to clrMapSetItem leaves an `object` on the stack; the explicit `as V` forces the `unbox.any` narrowing ilemit
    // needs (mirrors the `x as T` -> unbox.any path). `(computed as Any?)` defeats the smart-cast so the cast is not elided.
    @Suppress("UNCHECKED_CAST")
    val narrowed = (computed as Any?) as V
    d.clrMapSetItem(key, narrowed)
    return narrowed
}

/**
 * `MutableMap.entries: MutableSet<MutableEntry<K,V>>` — the slot lowers to ICollection<MutableEntry>, which the
 * snapshot ArrayList's BCL List satisfies at runtime (the `as` is a lowered castclass onto ICollection). Entries are
 * LIVE (value reads and setValue go through the backing map); the set itself is a snapshot.
 */
public fun <K, V> clrMapMutableEntries(m: Any): MutableSet<MutableMap.MutableEntry<K, V>> {
    if (m is KotlinMutableMapSlots) return m.dotktMapMutableEntries() as MutableSet<MutableMap.MutableEntry<K, V>>
    val d = m as ClrRawDictionary
    val e = d.GetEnumerator()
    val out = LinkedHashSet<MutableMap.MutableEntry<K, V>>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(ClrMutableMapEntry(d, e.key() as K))
    return out
}

/**
 * Live `MutableMap.keys` view. The BCL Dictionary.KeyCollection is only an ICollection<K>, so it cannot carry the
 * independent MutableSet identity. This Kotlin view does, while forwarding remove/clear to the backing raw map.
 * Adding a key has no corresponding value and is therefore unsupported, matching Kotlin's MutableMap.keys contract.
 */
public fun <K, V> clrMapMutableKeys(m: Any): MutableSet<K> =
    if (m is KotlinMutableMapSlots) m.dotktMapMutableKeys() as MutableSet<K> else ClrMutableMapKeySet(m as ClrRawDictionary)

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IDictionary")
private interface ClrNativeMapValues<K, V> {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Values")
    val values: MutableCollection<V>
}

public fun <K, V> clrMapMutableValues(m: Any): MutableCollection<V> =
    if (m is KotlinMutableMapSlots) m.dotktMapMutableValues() as MutableCollection<V>
    else (m as ClrNativeMapValues<K, V>).values

public fun <K, V> clrMapClear(m: Any) {
    if (m is KotlinMutableMapSlots) m.dotktMapClear() else (m as ClrRawDictionary).rawClear()
}

// ---- pure-Kotlin backing types --------------------------------------------------------------------------------------

/** A LIVE mutable entry over the backing map, held as the NON-GENERIC IDictionary (raw): value reads (get_Item) and
 *  setValue (set_Item) dispatch covariance-safely, so an entry surfaced from a groupBy result
 *  (`Dictionary<K,IList<V>>` read as `IDictionary<K,IReadOnlyList<V>>`) never hits the generic value-type invariance.
 *  Key/value narrow with `as K`/`as V` (unbox.any for value types). */
private class ClrMutableMapEntry<K, V>(private val raw: ClrRawDictionary, override val key: K) :
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

/** A pure-Kotlin `Set` over a snapshot list (mirrors ClrSubList: named class, NOT an object expression — kotc cannot
 *  yet lower an object expression capturing an enclosing generic parameter). Backs Map's read views. */
private class ClrMapSnapshotSet<E>(private val elements: List<E>) : Set<E> {
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 1)
    override fun hashCode(): Int = DotKt.Runtime.CompilerServices.structuralHash(this, 1)

    override val size: Int get() = elements.size
    override fun isEmpty(): Boolean = elements.size == 0
    override fun contains(element: E): Boolean = clrCollContains(elements, element)
    override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this.elements, elements)
    override fun iterator(): Iterator<E> = clrListListIterator(elements, 0)
}

private class ClrMutableMapKeySet<K>(private val raw: ClrRawDictionary) : MutableSet<K> {
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 1)
    override fun hashCode(): Int = DotKt.Runtime.CompilerServices.structuralHash(this, 1)

    override val size: Int get() = (raw as ClrRawCollection).count()
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
        while (e.MoveNext()) snapshot.add(e.key() as K)
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
        while (e.MoveNext()) {
            val key = e.key() as K
            if (!elements.contains(key)) remove.add(key)
        }
        var changed = false
        for (key in remove) if (raw.clrMapRemoveKey(key)) changed = true
        return changed
    }
    override fun clear() = raw.rawClear()
}
