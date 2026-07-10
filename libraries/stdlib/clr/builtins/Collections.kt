/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual` declarations.
// Bodies are TODO pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.collections

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerable")
public actual interface Iterable<out T> {
    public actual operator fun iterator(): Iterator<T>
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerable")
public actual interface MutableIterable<out T> : Iterable<T> {
    actual override fun iterator(): MutableIterator<T>
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IReadOnlyCollection")
public actual interface Collection<out E> : Iterable<E> {
    @kotlin.clr.ClrIntrinsic("Count") public actual val size: Int
    public actual fun isEmpty(): Boolean
    public actual operator fun contains(element: @UnsafeVariance E): Boolean
    actual override fun iterator(): Iterator<E>
    public actual fun containsAll(elements: Collection<@UnsafeVariance E>): Boolean
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.ICollection")
public actual interface MutableCollection<E> : Collection<E>, MutableIterable<E> {
    actual override fun iterator(): MutableIterator<E>
    @kotlin.clr.ClrIntrinsic("Add") public actual fun add(element: E): Boolean
    @kotlin.clr.ClrIntrinsic("Remove") public actual fun remove(element: E): Boolean
    public actual fun addAll(elements: Collection<E>): Boolean
    public actual fun removeAll(elements: Collection<E>): Boolean
    public actual fun retainAll(elements: Collection<E>): Boolean
    @kotlin.clr.ClrIntrinsic("Clear") public actual fun clear(): Unit
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IReadOnlyList")
public actual interface List<out E> : Collection<E> {
    actual override val size: Int
    actual override fun isEmpty(): Boolean
    actual override fun contains(element: @UnsafeVariance E): Boolean
    actual override fun iterator(): Iterator<E>
    actual override fun containsAll(elements: Collection<@UnsafeVariance E>): Boolean
    @kotlin.clr.ClrIntrinsic("get_Item") public actual operator fun get(index: Int): E
    public actual fun indexOf(element: @UnsafeVariance E): Int
    public actual fun lastIndexOf(element: @UnsafeVariance E): Int
    public actual fun listIterator(): ListIterator<E>
    public actual fun listIterator(index: Int): ListIterator<E>
    public actual fun subList(fromIndex: Int, toIndex: Int): List<E>
    // getFirst/getLast/reversed are SYNTHESIZED onto List by kotc's SequencedCollectionInjector (a FIR fake-override),
    // so every implementer inherits them uniformly; declaring them here too would double-declare + clash.
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IList")
public actual interface MutableList<E> : List<E>, MutableCollection<E> {
    actual override fun add(element: E): Boolean
    actual override fun remove(element: E): Boolean
    actual override fun addAll(elements: Collection<E>): Boolean
    public actual fun addAll(index: Int, elements: Collection<E>): Boolean
    actual override fun removeAll(elements: Collection<E>): Boolean
    actual override fun retainAll(elements: Collection<E>): Boolean
    actual override fun clear(): Unit
    @kotlin.clr.ClrIntrinsic("set_Item") public actual operator fun set(index: Int, element: E): E
    @kotlin.clr.ClrIntrinsic("Insert") public actual fun add(index: Int, element: E): Unit
    @kotlin.clr.ClrIntrinsic("RemoveAt") public actual fun removeAt(index: Int): E
    actual override fun listIterator(): MutableListIterator<E>
    actual override fun listIterator(index: Int): MutableListIterator<E>
    actual override fun subList(fromIndex: Int, toIndex: Int): MutableList<E>
    actual override fun iterator(): MutableIterator<E>
    // addFirst/addLast/removeFirst/removeLast are SYNTHESIZED onto MutableList by kotc's SequencedCollectionInjector
    // (a FIR fake-override); declaring them here too would double-declare + clash (EnumEntriesList).
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IReadOnlyCollection")
public actual interface Set<out E> : Collection<E> {
    actual override val size: Int
    actual override fun isEmpty(): Boolean
    actual override fun contains(element: @UnsafeVariance E): Boolean
    actual override fun iterator(): Iterator<E>
    actual override fun containsAll(elements: Collection<@UnsafeVariance E>): Boolean
}

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.ICollection")
public actual interface MutableSet<E> : Set<E>, MutableCollection<E> {
    actual override fun iterator(): MutableIterator<E>
    actual override fun add(element: E): Boolean
    actual override fun remove(element: E): Boolean
    actual override fun addAll(elements: Collection<E>): Boolean
    actual override fun removeAll(elements: Collection<E>): Boolean
    actual override fun retainAll(elements: Collection<E>): Boolean
    actual override fun clear(): Unit
}

// Map AND MutableMap BOTH alias System.Collections.Generic.IDictionary — deliberately NOT the
// IReadOnlyDictionary/IDictionary split that would mirror List/MutableList: BCL IDictionary does NOT extend
// IReadOnlyDictionary, so a split pair breaks Kotlin's `MutableMap : Map` subtyping at the IL level (a MutableMap-typed
// value in a Map-typed slot is formally unverifiable — the same latent hole the IList->IReadOnlyList pair already has,
// but here it would sit on the HOT path: every inherited Map member called on a MutableMap receiver). Collapsing both
// onto IDictionary keeps every store/param-pass verifier-clean, keeps `M : MutableMap<K,V>` constraints satisfiable by
// the concrete Dictionary (mapOf/mutableMapOf/associateTo), and mirrors Kotlin/JVM, where both erase to java.util.Map —
// read-only-ness is Kotlin-frontend-enforced. In-repo precedent: Iterable/MutableIterable both alias IEnumerable.
// Members with a direct IDictionary equivalent carry @ClrIntrinsic; the rest (null-on-missing `get`, the keys/values/
// entries VIEWS, putAll/getOrDefault/...) route through kotlin.collections.ClrMapDefaults (bir2cir Rule 5).
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IDictionary")
public actual interface Map<K, out V> {
    // size / containsKey are NOT bound to @ClrIntrinsic("Count") / @ClrIntrinsic("ContainsKey"): a DIRECT bind reads
    // through the INVARIANT generic IDictionary<K,V>, which throws EntryPointNotFound on a value-type-mismatched map
    // (a groupBy result: runtime Dictionary<K,IList<V>> read as IDictionary<K,IReadOnlyList<V>>). Instead bir2cir Rule 5m
    // routes `get_size`/`containsKey` on a Map/MutableMap owner to the COVARIANCE-SAFE ClrMapDefaults.clrMapSize /
    // clrMapContainsKey (non-generic ICollection.Count / IDictionary.Contains). This also makes `mapValues`' transitive
    // `mapCapacity(this.size)` covariance-safe. (isEmpty is already routed to clrMapIsEmpty via Rule 5m.)
    public actual val size: Int
    public actual fun isEmpty(): Boolean
    public actual fun containsKey(key: K): Boolean
    public actual fun containsValue(value: @UnsafeVariance V): Boolean
    // Kotlin `get` returns null on a missing key; IDictionary's get_Item THROWS. NOT bound — routed to
    // ClrMapDefaults.clrMapGet (ContainsKey + raw get_Item).
    public actual operator fun get(key: K): V?
    // Set/Collection-typed read views: `Set` is a PURE Kotlin interface (unaliased), so these cannot bind to
    // IDictionary.Keys/.Values (KeyCollection/ValueCollection do not implement it) — routed to ClrMapDefaults snapshots.
    public actual val keys: Set<K>
    public actual val values: Collection<V>
    public actual val entries: Set<Map.Entry<K, V>>

    // java.util.Map.getOrDefault leaks onto kotlin.collections.Map (the compiler injects it from the builtin). Give it a
    // DEFAULT body here so concrete CLR maps (EmptyMap/AbstractMutableMap) inherit it as a default interface method.
    @SinceKotlin("1.1")
    public fun getOrDefault(key: K, defaultValue: @UnsafeVariance V): @UnsafeVariance V {
        val v = get(key)
        @Suppress("UNCHECKED_CAST")
        return if (v != null || containsKey(key)) v as V else defaultValue
    }

    public actual interface Entry<out K, out V> {
        public actual val key: K
        public actual val value: V
    }
}

// Same alias as Map (see the rationale above) — the pair collapses onto IDictionary.
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IDictionary")
public actual interface MutableMap<K, V> : Map<K, V> {
    // put/remove return the PREVIOUS value (V?), which set_Item/Remove don't — routed to ClrMapDefaults wrappers.
    public actual fun put(key: K, value: V): V?
    public actual fun remove(key: K): V?
    public actual fun putAll(from: Map<out K, V>): Unit
    @kotlin.clr.ClrIntrinsic("Clear")
    public actual fun clear(): Unit

    // java.util.Map mutation defaults the compiler injects onto kotlin.collections.MutableMap (not in this source) as
    // ABSTRACT -> concrete maps would fail to load ("does not have an implementation"). Give them DEFAULT bodies (DIM).
    @SinceKotlin("1.1")
    public fun remove(key: K, value: V): Boolean =
        if (containsKey(key) && get(key) == value) { remove(key); true } else false
    @SinceKotlin("1.1")
    public fun putIfAbsent(key: K, value: V): V? {
        val v = get(key)
        return if (v == null) put(key, value) else v
    }
    @SinceKotlin("1.1")
    public fun replace(key: K, value: V): V? = if (containsKey(key)) put(key, value) else null
    @SinceKotlin("1.1")
    public fun replace(key: K, oldValue: V, newValue: V): Boolean =
        if (containsKey(key) && get(key) == oldValue) { put(key, newValue); true } else false
    // java.util.Map.merge leaks onto kotlin.collections.MutableMap as a member taking a `java.util.function.BiFunction`
    // (C2). On the CLR that erased SAM materializes the Kotlin lambda as `Func<V,V,object>` then `castclass`es it to the
    // `? super V`-erased `Func<object,object,object>` -> InvalidCastException. DECLARING merge here with a Kotlin
    // function-type parameter makes the frontend bind to THIS overload (function-type beats SAM), so the lambda flows
    // through with its precise `Func<V,V,object>` shape and no cast. DEFAULT body (DIM) so concrete CLR maps inherit it;
    // a BCL-aliased receiver (mutableMapOf) routes to ClrMapDefaults.clrMapMerge via bir2cir Rule 5m.
    @SinceKotlin("1.1")
    public fun merge(key: K, value: V, remappingFunction: (V, V) -> V?): V? {
        val oldValue = get(key)
        val newValue = if (oldValue == null) value else remappingFunction(oldValue, value)
        if (newValue == null) remove(key) else put(key, newValue)
        return newValue
    }
    // The MUTABLE views' slot types lower to the BCL (MutableSet -> ICollection<K>, MutableCollection ->
    // ICollection<V>), which IDictionary.Keys/.Values satisfy directly — bind them, unlike Map's pure-Set-typed reads.
    @kotlin.clr.ClrIntrinsic("Keys")
    actual override val keys: MutableSet<K>
    @kotlin.clr.ClrIntrinsic("Values")
    actual override val values: MutableCollection<V>
    // No BCL equivalent (KeyValuePair enumeration has no live setValue) — routed to ClrMapDefaults.
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>>

    public actual interface MutableEntry<K, V> : Map.Entry<K, V> {
        public actual fun setValue(newValue: V): V
    }
}
