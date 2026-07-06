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

// ---- Map (read) defaults: COVARIANCE-SAFE via the NON-GENERIC System.Collections.IDictionary facade ---------------
//
// WHY non-generic: `groupBy` returns `Map<K, List<V>>` (aliased `IDictionary<K, IReadOnlyList<V>>`) but the runtime
// object it builds is a `Dictionary<K, MutableList<V>>` (`IDictionary<K, IList<V>>`). CLR `IDictionary<,>` is INVARIANT
// in the value, so the runtime map is NOT assignable to the generic read interface: a `get_Keys`/`get_Item`/`ContainsKey`
// dispatched through `IDictionary<K, IReadOnlyList<V>>` finds no slot on the runtime `Dictionary<K, IList<V>>` ->
// EntryPointNotFound (inside clrMapToString etc.). Reading through the NON-GENERIC `ClrRawDictionary`
// (System.Collections.IDictionary — implemented by EVERY `Dictionary<K,V>` regardless of V) decouples the read path from
// the generic value-type invariance. This is the read-side mirror of bir2cir's write-side MapVarianceRealign.
//
// Keys/values arrive boxed to `Any?` off the non-generic IDictionaryEnumerator / get_Item and are narrowed with `as K` /
// `as V` (unbox.any for value-type K/V). The MUTABLE helpers (put/remove/putAll/...) stay on the generic IDictionary<K,V>
// path: a genuine MutableMap has matching static/runtime V (groupBy's result is a read-only Map), so no mismatch arises
// there and the generic path avoids the box. (Direct `m.size`/`m.containsKey` — Rule 2 @ClrIntrinsic on the Map interface,
// not routed here — remain generic; not on groupBy's read surface.)

// Kotlin's Map.toString is `{a=1, b=2}` (AbstractMap.toString). The substituted BCL Dictionary renders its default
// `System.Collections.Generic.Dictionary`2[...]` instead, so the backend should route `map.toString()` / `println(map)`
// here (the map mirror of clrCollToString). Emitted into the rt assembly for kotc/bir2cir to target.
public fun <K, V> clrMapToString(m: Map<K, V>): String {
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

public fun <K, V> clrMapGet(m: Map<K, V>, key: K): V? {
    val d = m as ClrRawDictionary
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else null   // null flows DIRECTLY into the object-erased return (V?-NOTE)
}

public fun <K, V> clrMapIsEmpty(m: Map<K, V>): Boolean = (m as ClrRawCollection).count() == 0

// COVARIANCE-SAFE size / containsKey: these mirror `size`(@ClrIntrinsic "Count") and `containsKey`(@ClrIntrinsic
// "ContainsKey") on the Map interface, but read through the NON-GENERIC facade (ICollection.Count / IDictionary.Contains)
// so they survive a groupBy-style value-type mismatch. The Map interface members are still bound DIRECTLY (Rule 2) — a
// direct `m.size` / `m.containsKey(k)` on a mismatched map (e.g. groupBy's result) therefore still throws EntryPointNotFound,
// AND stdlib algorithms that pre-size via `this.size` (mapValues' `mapCapacity(size)`) hit it transitively. Making those
// covariance-safe needs bir2cir to route `get_size`/`containsKey` on a Map/MutableMap owner to these helpers (Rule 5m,
// exactly as `get`/`get_keys`/`get_values` already route) after unbinding their @ClrIntrinsic. Provided here as the
// ready targets for that route (currently un-called).
public fun <K, V> clrMapSize(m: Map<K, V>): Int = (m as ClrRawCollection).count()

public fun <K, V> clrMapContainsKey(m: Map<K, V>, key: K): Boolean = (m as ClrRawDictionary).Contains(key)

public fun <K, V> clrMapContainsValue(m: Map<K, V>, value: V): Boolean {
    val e = (m as ClrRawDictionary).GetEnumerator()
    while (e.MoveNext()) if (e.value() == value) return true
    return false
}

public fun <K, V> clrMapGetOrDefault(m: Map<K, V>, key: K, defaultValue: V): V {
    val d = m as ClrRawDictionary
    @Suppress("UNCHECKED_CAST")
    return if (d.Contains(key)) (d.rawGet(key) as V) else defaultValue
}

/** `Map.keys: Set<K>` — Set is PURE Kotlin (unaliased), so IDictionary.Keys can't back it; snapshot into a pure Set. */
public fun <K, V> clrMapKeys(m: Map<K, V>): Set<K> {
    val e = (m as ClrRawDictionary).GetEnumerator()
    val out = ArrayList<K>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(e.key() as K)
    return ClrMapSnapshotSet(out)
}

/** `Map.values: Collection<V>` — an ArrayList (BCL List) IS a Collection (IReadOnlyCollection) — snapshot. */
public fun <K, V> clrMapValues(m: Map<K, V>): Collection<V> {
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
public fun <K, V> clrMapEntries(m: Map<K, V>): Set<Map.Entry<K, V>> {
    val d = m as ClrRawDictionary   // non-generic: a generic `m as MutableMap<K,V>` would castclass to the invariant
    val e = d.GetEnumerator()       // IDictionary<K,IReadOnlyList<V>> and FAIL on a groupBy Dictionary<K,IList<V>>.
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
 * `MutableMap.merge(key, value, remappingFunction)` (C2 — the java.util.Map.merge equivalent): absent key -> insert
 * [value]; present -> [remappingFunction](old, value); a null result removes the entry. Structured like clrMapPutIfAbsent
 * so null only ever flows DIRECTLY into the (object-erased) return — never into a `gp:V` local (see the V?-return NOTE).
 */
public fun <K, V> clrMapMerge(m: MutableMap<K, V>, key: K, value: V, remappingFunction: (V, V) -> V?): V? {
    if (!m.containsKey(key)) {
        m.clrMapSetItem(key, value)
        return value
    }
    val computed = remappingFunction(m.clrMapItem(key), value)
    if (computed == null) {
        m.clrMapRemoveKey(key)
        return null
    }
    // `computed` is smart-cast to V, but its CIR slot is the object-erased func return (`nullable:gp:V`). A plain store
    // to clrMapSetItem's `V` param leaves an `object` on the stack where a value `V` is expected (ilverify StackUnexpected /
    // runtime InvalidProgram) — the smart-cast emits no IL. The explicit `as V` forces the `unbox.any` narrowing ilemit
    // needs (mirrors the `x as T` -> unbox.any path). `(computed as Any?)` defeats the smart-cast so the cast is not elided.
    @Suppress("UNCHECKED_CAST")
    val narrowed = (computed as Any?) as V
    m.clrMapSetItem(key, narrowed)
    return narrowed
}

/**
 * `MutableMap.entries: MutableSet<MutableEntry<K,V>>` — the slot lowers to ICollection<MutableEntry>, which the
 * snapshot ArrayList's BCL List satisfies at runtime (the `as` is a lowered castclass onto ICollection). Entries are
 * LIVE (value reads and setValue go through the backing map); the set itself is a snapshot.
 */
public fun <K, V> clrMapMutableEntries(m: MutableMap<K, V>): MutableSet<MutableMap.MutableEntry<K, V>> {
    val d = m as ClrRawDictionary
    val e = d.GetEnumerator()
    val out = ArrayList<MutableMap.MutableEntry<K, V>>()
    @Suppress("UNCHECKED_CAST")
    while (e.MoveNext()) out.add(ClrMutableMapEntry(d, e.key() as K))
    return (out as Any) as MutableSet<MutableMap.MutableEntry<K, V>>
}

// ---- pure-Kotlin backing types --------------------------------------------------------------------------------------

/** A LIVE mutable entry over the backing map, held as the NON-GENERIC IDictionary (raw): value reads (get_Item) and
 *  setValue (set_Item) dispatch covariance-safely, so an entry surfaced from a groupBy result
 *  (`Dictionary<K,IList<V>>` read as `IDictionary<K,IReadOnlyList<V>>`) never hits the generic value-type invariance —
 *  a generic `MutableMap<K,V>`-typed field would already have castclass-failed at construction. Key/value narrow with
 *  `as K`/`as V` (unbox.any for value types). */
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
