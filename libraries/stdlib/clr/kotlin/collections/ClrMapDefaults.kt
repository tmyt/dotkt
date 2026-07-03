/*
 * CLR ref/runtime split: default implementations for Map/MutableMap members that have NO 1:1 equivalent on the
 * substituted BCL IDictionary<K,V> (see builtins/Collections.kt for the alias rationale). The backend (bir2cir Rule 5,
 * 2-type-arg map routing) rewrites `m.get(k)` / `m.put(k,v)` / `m.entries` / ... to these statics — the map mirror of
 * ClrCollectionDefaults. The bodies use ONLY BCL-bound members: the @ClrIntrinsic members on the Map/MutableMap
 * interfaces (containsKey -> ContainsKey, size -> Count, clear -> Clear) plus the raw extension intrinsics below
 * (clrMapItem -> get_Item, clrMapSetItem -> set_Item, ...), so they never recurse into a routed member.
 *
 * Semantics notes (recorded in docs/dotkt-semantics.md):
 *   - `Map.get` is null-on-missing (Kotlin), synthesized as ContainsKey + get_Item (IDictionary's get_Item throws).
 *   - Map's READ views (keys/values/entries: pure-Kotlin Set/Collection types) are SNAPSHOTS, not live views.
 *   - MutableMap.entries elements are LIVE (setValue writes through), but the entry SET itself is a snapshot.
 */
@file:Suppress("NOTHING_TO_INLINE", "UNCHECKED_CAST")

package kotlin.collections

// ---- raw BCL member bindings (metadata-only; bodies are never emitted in substitute builds) ----------------------

/** Raw IDictionary get_Item — THROWS on a missing key (the Kotlin-facing null-on-missing wrapper is [clrMapGet]). */
@kotlin.clr.ClrIntrinsic("get_Item")
public fun <K, V> Map<K, V>.clrMapItem(key: K): V = TODO("clr binding should be implemented")

/** Raw IDictionary set_Item (void; the previous-value-returning wrapper is [clrMapPut]). */
@kotlin.clr.ClrIntrinsic("set_Item")
public fun <K, V> MutableMap<K, V>.clrMapSetItem(key: K, value: V): Unit = TODO("clr binding should be implemented")

/** Raw IDictionary Remove(key): Boolean (the previous-value-returning wrapper is [clrMapRemove]). */
@kotlin.clr.ClrIntrinsic("Remove")
public fun <K, V> MutableMap<K, V>.clrMapRemoveKey(key: K): Boolean = TODO("clr binding should be implemented")

/** Raw IDictionary Keys (ICollection<K>, surfaced as the aliased Iterable -> IEnumerable<K>). */
@kotlin.clr.ClrIntrinsic("get_Keys")
public fun <K, V> Map<K, V>.clrMapNativeKeys(): Iterable<K> = TODO("clr binding should be implemented")

// ---- Map (read) defaults ------------------------------------------------------------------------------------------

// Kotlin's Map.toString is `{a=1, b=2}` (AbstractMap.toString). The substituted BCL Dictionary renders its default
// `System.Collections.Generic.Dictionary`2[...]` instead, so the backend should route `map.toString()` / `println(map)`
// here (the map mirror of clrCollToString). Emitted into the rt assembly for kotc/bir2cir to target.
public fun <K, V> clrMapToString(m: Map<K, V>): String {
    val sb = StringBuilder()
    sb.append("{")
    var first = true
    for (k in m.clrMapNativeKeys()) {
        if (!first) sb.append(", ")
        first = false
        sb.append(k.toString())
        sb.append("=")
        sb.append(m.clrMapItem(k).toString())
    }
    sb.append("}")
    return sb.toString()
}

public fun <K, V> clrMapGet(m: Map<K, V>, key: K): V? = if (m.containsKey(key)) m.clrMapItem(key) else null

public fun <K, V> clrMapIsEmpty(m: Map<K, V>): Boolean = m.size == 0

public fun <K, V> clrMapContainsValue(m: Map<K, V>, value: V): Boolean {
    for (k in m.clrMapNativeKeys()) if (m.clrMapItem(k) == value) return true
    return false
}

public fun <K, V> clrMapGetOrDefault(m: Map<K, V>, key: K, defaultValue: V): V =
    if (m.containsKey(key)) m.clrMapItem(key) else defaultValue

/** `Map.keys: Set<K>` — Set is PURE Kotlin (unaliased), so IDictionary.Keys can't back it; snapshot into a pure Set. */
public fun <K, V> clrMapKeys(m: Map<K, V>): Set<K> {
    val out = ArrayList<K>()
    for (k in m.clrMapNativeKeys()) out.add(k)
    return ClrMapSnapshotSet(out)
}

/** `Map.values: Collection<V>` — an ArrayList (BCL List) IS a Collection (IReadOnlyCollection) — snapshot. */
public fun <K, V> clrMapValues(m: Map<K, V>): Collection<V> {
    val out = ArrayList<V>()
    for (k in m.clrMapNativeKeys()) out.add(m.clrMapItem(k))
    return out
}

/** `Map.entries: Set<Map.Entry<K,V>>` — a pure-Set snapshot of LIVE entries (value reads go through the map). The
 *  element impl is ClrMutableMapEntry (implements MutableEntry TOO), so an entry surfaced through either the Map or
 *  the MutableMap view destructures/casts fine — at runtime every aliased map IS an IDictionary. */
public fun <K, V> clrMapEntries(m: Map<K, V>): Set<Map.Entry<K, V>> {
    val mm = m as MutableMap<K, V>   // representation cast: Map and MutableMap both lower to IDictionary<K,V>
    val out = ArrayList<Map.Entry<K, V>>()
    for (k in mm.clrMapNativeKeys()) out.add(ClrMutableMapEntry(mm, k))
    return ClrMapSnapshotSet(out)
}

// ---- MutableMap defaults ------------------------------------------------------------------------------------------

// NOTE (all V?-returning wrappers): never hold a `V?` in a LOCAL — a nullable unconstrained-generic local erases to
// a bare `gp:V` slot, and a null flowing into it `unbox.any`s to NRE when V is instantiated with a value type (the
// RC2 object-erasure covers the RETURN boundary, not locals). Structure the bodies so null only ever flows directly
// into the (object-erased) return.

public fun <K, V> clrMapPut(m: MutableMap<K, V>, key: K, value: V): V? {
    if (m.containsKey(key)) {
        val old = m.clrMapItem(key)
        m.clrMapSetItem(key, value)
        return old
    }
    m.clrMapSetItem(key, value)
    return null
}

public fun <K, V> clrMapRemove(m: MutableMap<K, V>, key: K): V? {
    if (m.containsKey(key)) {
        val old = m.clrMapItem(key)
        m.clrMapRemoveKey(key)
        return old
    }
    return null
}

public fun <K, V> clrMapRemoveKV(m: MutableMap<K, V>, key: K, value: V): Boolean =
    if (m.containsKey(key) && m.clrMapItem(key) == value) { m.clrMapRemoveKey(key); true } else false

public fun <K, V> clrMapPutAll(m: MutableMap<K, V>, from: Map<out K, V>): Unit {
    val src = from as Map<K, V>   // the out-projection prohibits clrMapItem's K-in-in-position receiver; erased anyway
    for (k in src.clrMapNativeKeys()) m.clrMapSetItem(k, src.clrMapItem(k))
}

public fun <K, V> clrMapPutIfAbsent(m: MutableMap<K, V>, key: K, value: V): V? =
    if (m.containsKey(key)) m.clrMapItem(key) else { m.clrMapSetItem(key, value); null }

public fun <K, V> clrMapReplace(m: MutableMap<K, V>, key: K, value: V): V? =
    if (m.containsKey(key)) clrMapPut(m, key, value) else null

public fun <K, V> clrMapReplaceKVV(m: MutableMap<K, V>, key: K, oldValue: V, newValue: V): Boolean =
    if (m.containsKey(key) && m.clrMapItem(key) == oldValue) { m.clrMapSetItem(key, newValue); true } else false

/**
 * `MutableMap.entries: MutableSet<MutableEntry<K,V>>` — the slot lowers to ICollection<MutableEntry>, which the
 * snapshot ArrayList's BCL List satisfies at runtime (the `as` is a lowered castclass onto ICollection). Entries are
 * LIVE (value reads and setValue go through the backing map); the set itself is a snapshot.
 */
public fun <K, V> clrMapMutableEntries(m: MutableMap<K, V>): MutableSet<MutableMap.MutableEntry<K, V>> {
    val out = ArrayList<MutableMap.MutableEntry<K, V>>()
    for (k in m.clrMapNativeKeys()) out.add(ClrMutableMapEntry(m, k))
    return (out as Any) as MutableSet<MutableMap.MutableEntry<K, V>>
}

// ---- pure-Kotlin backing types --------------------------------------------------------------------------------------

/** A LIVE mutable entry over the backing map: value reads and setValue dispatch through the raw item intrinsics. */
private class ClrMutableMapEntry<K, V>(private val map: MutableMap<K, V>, override val key: K) :
    MutableMap.MutableEntry<K, V> {
    override val value: V get() = map.clrMapItem(key)
    override fun setValue(newValue: V): V {
        val old = map.clrMapItem(key)
        map.clrMapSetItem(key, newValue)
        return old
    }
    override fun toString(): String = key.toString() + "=" + value.toString()
}

/** A pure-Kotlin `Set` over a snapshot list (mirrors ClrSubList: named class, NOT an object expression — kotc cannot
 *  yet lower an object expression capturing an enclosing generic parameter). Backs Map's read views. */
private class ClrMapSnapshotSet<E>(private val elements: List<E>) : Set<E> {
    override val size: Int get() = elements.size
    override fun isEmpty(): Boolean = elements.size == 0
    override fun contains(element: E): Boolean = clrCollContains(elements, element)
    override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this.elements, elements)
    override fun iterator(): Iterator<E> = clrListListIterator(elements, 0)
}
