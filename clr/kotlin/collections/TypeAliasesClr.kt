/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file (typealiases expanded to stub classes/interface).
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Marker interface indicating that the [List] implementation supports fast indexed access.
 */
@SinceKotlin("1.1")
public actual interface RandomAccess


@SinceKotlin("1.1")
@kotlin.clr.ClrIntrinsic("System.Collections.Generic.List")
public actual class ArrayList<E> : MutableList<E>, RandomAccess {
    public actual constructor()
    public actual constructor(initialCapacity: Int)
    public actual constructor(elements: Collection<E>)

    @kotlin.clr.ClrIntrinsic("TrimExcess")
    public actual fun trimToSize() { TODO("clr binding should be implemented") }
    public actual fun ensureCapacity(minCapacity: Int) { TODO("clr binding should be implemented") }

    // From List

    @kotlin.clr.ClrIntrinsic("Count")
    actual override val size: Int get() = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = size == 0
    @kotlin.clr.ClrIntrinsic("Contains")
    actual override fun contains(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun containsAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("get_Item")
    actual override operator fun get(index: Int): E = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("IndexOf")
    actual override fun indexOf(element: E): Int = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("LastIndexOf")
    actual override fun lastIndexOf(element: E): Int = TODO("clr binding should be implemented")

    // From MutableCollection

    public actual override fun iterator(): MutableIterator<E> = TODO("clr binding should be implemented")

    // From MutableList

    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(index: Int, elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun removeAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun retainAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("set_Item")
    actual override operator fun set(index: Int, element: E): E = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Insert")
    actual override fun add(index: Int, element: E) { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("RemoveAt")
    actual override fun removeAt(index: Int): E = TODO("clr binding should be implemented")
    actual override fun listIterator(): MutableListIterator<E> = TODO("clr binding should be implemented")
    actual override fun listIterator(index: Int): MutableListIterator<E> = TODO("clr binding should be implemented")
    actual override fun subList(fromIndex: Int, toIndex: Int): MutableList<E> = TODO("clr binding should be implemented")
}


@SinceKotlin("1.1")
@kotlin.clr.ClrIntrinsic("System.Collections.Generic.Dictionary")
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

    @kotlin.clr.ClrIntrinsic("set_Item")
    private fun nativeSet(key: K, value: V): Unit = TODO("clr binding should be implemented")
    actual override fun put(key: K, value: V): V? { val old = get(key); nativeSet(key, value); return old }
    @kotlin.clr.ClrIntrinsic("Remove")
    private fun nativeRemove(key: K): Boolean = TODO("clr binding should be implemented")
    actual override fun remove(key: K): V? { val old = get(key); nativeRemove(key); return old }
    actual override fun putAll(from: Map<out K, V>) { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("Keys")
    actual override val keys: MutableSet<K> get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Values")
    actual override val values: MutableCollection<V> get() = TODO("clr binding should be implemented")
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = TODO("clr binding should be implemented")
}


@SinceKotlin("1.1")
@kotlin.clr.ClrIntrinsic("System.Collections.Generic.Dictionary")
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

    @kotlin.clr.ClrIntrinsic("set_Item")
    private fun nativeSet(key: K, value: V): Unit = TODO("clr binding should be implemented")
    actual override fun put(key: K, value: V): V? { val old = get(key); nativeSet(key, value); return old }
    @kotlin.clr.ClrIntrinsic("Remove")
    private fun nativeRemove(key: K): Boolean = TODO("clr binding should be implemented")
    actual override fun remove(key: K): V? { val old = get(key); nativeRemove(key); return old }
    actual override fun putAll(from: Map<out K, V>) { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
    @kotlin.clr.ClrIntrinsic("Keys")
    actual override val keys: MutableSet<K> get() = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Values")
    actual override val values: MutableCollection<V> get() = TODO("clr binding should be implemented")
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>> get() = TODO("clr binding should be implemented")
}


@SinceKotlin("1.1")
@kotlin.clr.ClrIntrinsic("System.Collections.Generic.HashSet")
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
    actual override fun containsAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")

    // From MutableSet

    actual override fun iterator(): MutableIterator<E> = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun removeAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun retainAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
}


@SinceKotlin("1.1")
@kotlin.clr.ClrIntrinsic("System.Collections.Generic.HashSet")
public actual class LinkedHashSet<E> : MutableSet<E> {
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
    actual override fun containsAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")

    // From MutableSet

    actual override fun iterator(): MutableIterator<E> = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Add")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Remove")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun removeAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun retainAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
}
