/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// CLR actuals for the JVM `typealias ArrayList/HashMap/LinkedHashMap/HashSet/LinkedHashSet` declarations.
// Each collection class is `@ClrIntrinsic`-bound to its BCL counterpart (List / Dictionary / HashSet). Members with a
// clean 1:1 BCL equivalent carry `@ClrIntrinsic("<member>")` and keep a `TODO` body (pure metadata, never emitted).
// Members with no 1:1 equivalent (containsAll/addAll/iterator/subList/keys/entries, plus the return-the-old-element
// `set`/`removeAt` wrappers) carry a REAL Kotlin body (Rule 3): it must use only the intrinsic siblings.

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Marker interface indicating that the [List] implementation supports fast indexed access.
 */
@SinceKotlin("1.1")
public actual interface RandomAccess


@SinceKotlin("1.1")
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.List")
public actual class ArrayList<E> : MutableList<E>, RandomAccess {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(elements: Collection<E>)

    @kotlin.clr.ClrIntrinsic("TrimExcess")
    public actual fun trimToSize() { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("EnsureCapacity")
    public actual fun ensureCapacity(minCapacity: Int) { TODO("clr binding should be implemented") }

    // From List

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("Contains")
    actual override fun contains(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this, elements)
    @kotlin.clr.ClrIntrinsic("get_Item")
    actual override operator fun get(index: Int): E = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("IndexOf")
    actual override fun indexOf(element: E): Int = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("LastIndexOf")
    actual override fun lastIndexOf(element: E): Int = TODO("clr binding should be implemented")

    // From MutableCollection

    public actual override fun iterator(): MutableIterator<E> = ArrayListIterator(this, 0)

    // From MutableList

    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (add(element)) modified = true }
        return modified
    }
    actual override fun addAll(index: Int, elements: Collection<E>): Boolean {
        var insertIndex = index
        var changed = false
        for (element in elements) {
            add(insertIndex, element)
            insertIndex++
            changed = true
        }
        return changed
    }
    actual override fun removeAll(elements: Collection<E>): Boolean {
        var modified = false
        var i = 0
        while (i < size) {
            if (elements.contains(get(i))) { removeAt(i); modified = true } else { i++ }
        }
        return modified
    }
    actual override fun retainAll(elements: Collection<E>): Boolean {
        var modified = false
        var i = 0
        while (i < size) {
            if (!elements.contains(get(i))) { removeAt(i); modified = true } else { i++ }
        }
        return modified
    }
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }

    // set(index, E): the BCL indexer setter `set_Item` is void, but Kotlin's `set` returns the PREVIOUS element.
    // Split: a private void native carrying the intrinsic + a bodied wrapper that captures the old value.
    @kotlin.clr.ClrIntrinsic("set_Item")
    private fun nativeSet(index: Int, element: E) { TODO("clr binding should be implemented") }
    actual override operator fun set(index: Int, element: E): E {
        val old = get(index)
        nativeSet(index, element)
        return old
    }

    @kotlin.clr.ClrIntrinsic("Insert")
    actual override fun add(index: Int, element: E) { TODO("clr binding should be implemented") }

    // removeAt(index): the BCL `RemoveAt` is void, but Kotlin returns the REMOVED element. Same split as `set`.
    @kotlin.clr.ClrIntrinsic("RemoveAt")
    private fun nativeRemoveAt(index: Int) { TODO("clr binding should be implemented") }
    actual override fun removeAt(index: Int): E {
        val old = get(index)
        nativeRemoveAt(index)
        return old
    }

    actual override fun listIterator(): MutableListIterator<E> = ArrayListIterator(this, 0)
    actual override fun listIterator(index: Int): MutableListIterator<E> = ArrayListIterator(this, index)
    actual override fun subList(fromIndex: Int, toIndex: Int): MutableList<E> = ArrayListSubList(this, fromIndex, toIndex)
}

/** A live `MutableListIterator` over an [ArrayList], built from the intrinsic size/get/set/add(index)/removeAt members. */
private class ArrayListIterator<E>(private val list: ArrayList<E>, startIndex: Int) : MutableListIterator<E> {
    private var cursor: Int = startIndex
    private var last: Int = -1

    override fun hasNext(): Boolean = cursor < list.size
    override fun next(): E {
        if (cursor >= list.size) throw NoSuchElementException()
        last = cursor
        cursor++
        return list[last]
    }
    override fun hasPrevious(): Boolean = cursor > 0
    override fun nextIndex(): Int = cursor
    override fun previousIndex(): Int = cursor - 1
    override fun previous(): E {
        if (cursor <= 0) throw NoSuchElementException()
        cursor--
        last = cursor
        return list[last]
    }
    override fun remove() {
        if (last == -1) throw IllegalStateException("Call next() or previous() before removing element from the iterator.")
        list.removeAt(last)
        if (last < cursor) cursor--
        last = -1
    }
    override fun set(element: E) {
        if (last == -1) throw IllegalStateException("Call next() or previous() before updating element value with the iterator.")
        list.set(last, element)
    }
    override fun add(element: E) {
        list.add(cursor, element)
        cursor++
        last = -1
    }
}

/** A live mutable sub-range view of an [ArrayList]; reuses AbstractMutableList's machinery over the backing list. */
private class ArrayListSubList<E>(
    private val list: ArrayList<E>,
    private val fromIndex: Int,
    toIndex: Int
) : AbstractMutableList<E>(), RandomAccess {
    private var _size: Int = toIndex - fromIndex

    override fun add(index: Int, element: E) {
        list.add(fromIndex + index, element)
        _size++
    }
    override fun get(index: Int): E = list[fromIndex + index]
    override fun removeAt(index: Int): E {
        val result = list.removeAt(fromIndex + index)
        _size--
        return result
    }
    override fun set(index: Int, element: E): E = list.set(fromIndex + index, element)
    override val size: Int get() = _size
}


@SinceKotlin("1.1")
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.Dictionary")
public actual class HashMap<K, V> : MutableMap<K, V> {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(initialCapacity: Int, loadFactor: Float)
    public actual constructor(original: Map<out K, V>)

    // From Map

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("ContainsKey")
    actual override fun containsKey(key: K): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("ContainsValue")
    actual override fun containsValue(value: V): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("get_Item")
    private fun nativeGet(key: K): V = TODO("clr binding should be implemented")
    actual override operator fun get(key: K): V? = if (containsKey(key)) nativeGet(key) else null

    // From MutableMap

    // NOTE (put/remove): never hold the previous value as a `V?` LOCAL — a nullable unconstrained-generic local
    // erases to a bare `gp:V` slot and a null flowing into it is invalid IL / NRE for a value instantiation (the
    // same landmine documented on ClrMapDefaults). Structure the bodies so only a KNOWN-PRESENT value (guarded by
    // containsKey) ever sits in a V-typed local; the null flows directly into the object-erased return.
    @kotlin.clr.ClrIntrinsic("set_Item")
    private fun nativeSet(key: K, value: V): Unit = TODO("clr binding should be implemented")
    actual override fun put(key: K, value: V): V? {
        if (containsKey(key)) {
            val old = nativeGet(key)
            nativeSet(key, value)
            return old
        }
        nativeSet(key, value)
        return null
    }
    @kotlin.clr.ClrIntrinsic("Remove")
    private fun nativeRemove(key: K): Boolean = TODO("clr binding should be implemented")
    actual override fun remove(key: K): V? {
        if (containsKey(key)) {
            val old = nativeGet(key)
            nativeRemove(key)
            return old
        }
        return null
    }
    actual override fun putAll(from: Map<out K, V>) = clrMapPutAll(this, from)
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }

    // MutableSet<K>/MutableCollection<V> lower to ICollection<K>/<V>, which Dictionary.Keys/.Values (KeyCollection/
    // ValueCollection) implement directly — bind them. entries has no BCL equivalent -> the ClrMapDefaults snapshot
    // of LIVE entries (this replaced the old lambda-view machinery, whose generic-capturing object expressions the
    // backend could only stub to throw).
    @kotlin.clr.ClrIntrinsic("Keys")
    actual override val keys: MutableSet<K> get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Values")
    actual override val values: MutableCollection<V> get() = TODO("clr binding should be implemented")
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>>
        get() = clrMapMutableEntries(this)
}


@SinceKotlin("1.1")
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.Dictionary")
public actual class LinkedHashMap<K, V> : MutableMap<K, V> {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(initialCapacity: Int, loadFactor: Float)
    public actual constructor(original: Map<out K, V>)

    // From Map

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("ContainsKey")
    actual override fun containsKey(key: K): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("ContainsValue")
    actual override fun containsValue(value: V): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("get_Item")
    private fun nativeGet(key: K): V = TODO("clr binding should be implemented")
    actual override fun get(key: K): V? = if (containsKey(key)) nativeGet(key) else null

    // From MutableMap

    // put/remove: the null-safe containsKey-guarded shape — see the NOTE on HashMap above.
    @kotlin.clr.ClrIntrinsic("set_Item")
    private fun nativeSet(key: K, value: V): Unit = TODO("clr binding should be implemented")
    actual override fun put(key: K, value: V): V? {
        if (containsKey(key)) {
            val old = nativeGet(key)
            nativeSet(key, value)
            return old
        }
        nativeSet(key, value)
        return null
    }
    @kotlin.clr.ClrIntrinsic("Remove")
    private fun nativeRemove(key: K): Boolean = TODO("clr binding should be implemented")
    actual override fun remove(key: K): V? {
        if (containsKey(key)) {
            val old = nativeGet(key)
            nativeRemove(key)
            return old
        }
        return null
    }
    actual override fun putAll(from: Map<out K, V>) = clrMapPutAll(this, from)
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }

    // CAVEAT: plain Dictionary does not preserve insertion order, so this LinkedHashMap actual does not either.
    // Keys/Values bind directly (the lowered ICollection slots); entries -> the ClrMapDefaults live-entry snapshot.
    @kotlin.clr.ClrIntrinsic("Keys")
    actual override val keys: MutableSet<K> get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Values")
    actual override val values: MutableCollection<V> get() = TODO("clr binding should be implemented")
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>>
        get() = clrMapMutableEntries(this)
}


@SinceKotlin("1.1")
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.HashSet")
public actual class HashSet<E> : MutableSet<E> {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(initialCapacity: Int, loadFactor: Float)
    public actual constructor(elements: Collection<E>)

    // From Set

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("Contains")
    actual override fun contains(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this, elements)

    // From MutableSet

    // The BCL HashSet enumerator is a struct invalidated by mutation, so snapshot the elements first; remove() then
    // deletes the last-returned element from the live set. TODO(clr): a non-snapshot (live) mutable iterator.
    actual override fun iterator(): MutableIterator<E> {
        val snapshot = clrSetSnapshot(this)
        return object : MutableIterator<E> {
            private var index = 0
            private var lastEl: E? = null
            private var hasLast = false
            override fun hasNext(): Boolean = index < snapshot.size
            override fun next(): E {
                if (index >= snapshot.size) throw NoSuchElementException()
                val e = snapshot[index]
                index++
                lastEl = e
                hasLast = true
                return e
            }
            override fun remove() {
                if (!hasLast) throw IllegalStateException("next() has not been called")
                this@HashSet.remove(lastEl as E)
                hasLast = false
            }
        }
    }
    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (add(element)) modified = true }
        return modified
    }
    actual override fun removeAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (remove(element)) modified = true }
        return modified
    }
    actual override fun retainAll(elements: Collection<E>): Boolean {
        val toRemove = ArrayList<E>()
        val it = iterator()
        while (it.hasNext()) {
            val e = it.next()
            if (!elements.contains(e)) toRemove.add(e)
        }
        var modified = false
        for (e in toRemove) { if (remove(e)) modified = true }
        return modified
    }
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
}


@SinceKotlin("1.1")
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.HashSet")
public actual class LinkedHashSet<E> : MutableSet<E> {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(initialCapacity: Int, loadFactor: Float)
    public actual constructor(elements: Collection<E>)

    // From Set
    // CAVEAT: plain HashSet does not preserve insertion order, so this LinkedHashSet actual does not either.

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("Contains")
    actual override fun contains(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this, elements)

    // From MutableSet

    actual override fun iterator(): MutableIterator<E> {
        val snapshot = clrSetSnapshot(this)
        return object : MutableIterator<E> {
            private var index = 0
            private var lastEl: E? = null
            private var hasLast = false
            override fun hasNext(): Boolean = index < snapshot.size
            override fun next(): E {
                if (index >= snapshot.size) throw NoSuchElementException()
                val e = snapshot[index]
                index++
                lastEl = e
                hasLast = true
                return e
            }
            override fun remove() {
                if (!hasLast) throw IllegalStateException("next() has not been called")
                this@LinkedHashSet.remove(lastEl as E)
                hasLast = false
            }
        }
    }
    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (add(element)) modified = true }
        return modified
    }
    actual override fun removeAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (remove(element)) modified = true }
        return modified
    }
    actual override fun retainAll(elements: Collection<E>): Boolean {
        val toRemove = ArrayList<E>()
        val it = iterator()
        while (it.hasNext()) {
            val e = it.next()
            if (!elements.contains(e)) toRemove.add(e)
        }
        var modified = false
        for (e in toRemove) { if (remove(e)) modified = true }
        return modified
    }
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
}

/** Snapshot a CLR-backed set's elements into an ArrayList by enumerating the BCL IEnumerable directly (the bridge),
 *  avoiding recursion through the set's own `iterator()`. */
private fun <E> clrSetSnapshot(set: Collection<E>): ArrayList<E> {
    val out = ArrayList<E>()
    val source = iteratorOverEnumerable(set as ClrEnumerable<E>)
    while (source.hasNext()) out.add(source.next())
    return out
}
