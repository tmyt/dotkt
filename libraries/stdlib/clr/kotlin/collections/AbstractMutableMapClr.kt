/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Provides a skeletal implementation of the [MutableMap] interface.
 *
 * The implementor is required to implement [entries] property, which should return mutable set of map entries, and [put] function.
 *
 * @param K the type of map keys. The map is invariant in its key type.
 * @param V the type of map values. The map is invariant in its value type.
 */
@SinceKotlin("1.1")
public actual abstract class AbstractMutableMap<K, V> protected actual constructor() : MutableMap<K, V> {

    private fun implFindEntry(key: K): MutableMap.MutableEntry<K, V>? = entries.firstOrNull { it.key == key }

    private var _keys: MutableSet<K>? = null
    actual override val keys: MutableSet<K>
        get() {
            if (_keys == null) {
                _keys = object : AbstractMutableSet<K>() {
                    override fun add(element: K): Boolean = throw UnsupportedOperationException("Add is not supported on keys")

                    override fun clear() {
                        this@AbstractMutableMap.clear()
                    }

                    override operator fun contains(element: K): Boolean = containsKey(element)

                    override operator fun iterator(): MutableIterator<K> {
                        val entryIterator = entries.iterator()
                        return object : MutableIterator<K> {
                            override fun hasNext(): Boolean = entryIterator.hasNext()
                            override fun next(): K = entryIterator.next().key
                            override fun remove() = entryIterator.remove()
                        }
                    }

                    override fun remove(element: K): Boolean {
                        if (containsKey(element)) {
                            this@AbstractMutableMap.remove(element)
                            return true
                        }
                        return false
                    }

                    override val size: Int get() = this@AbstractMutableMap.size
                }
            }
            return _keys!!
        }

    actual override val size: Int
        get() = entries.size

    private var _values: MutableCollection<V>? = null
    actual override val values: MutableCollection<V>
        get() {
            if (_values == null) {
                _values = object : AbstractMutableCollection<V>() {
                    override fun add(element: V): Boolean = throw UnsupportedOperationException("Add is not supported on values")

                    override fun clear() {
                        this@AbstractMutableMap.clear()
                    }

                    override operator fun contains(element: V): Boolean = containsValue(element)

                    override operator fun iterator(): MutableIterator<V> {
                        val entryIterator = entries.iterator()
                        return object : MutableIterator<V> {
                            override fun hasNext(): Boolean = entryIterator.hasNext()
                            override fun next(): V = entryIterator.next().value
                            override fun remove() = entryIterator.remove()
                        }
                    }

                    override val size: Int get() = this@AbstractMutableMap.size
                }
            }
            return _values!!
        }

    actual override fun clear(): Unit {
        entries.clear()
    }

    actual override fun containsKey(key: K): Boolean = implFindEntry(key) != null
    actual override fun containsValue(value: V): Boolean = entries.any { it.value == value }
    actual override fun get(key: K): V? = implFindEntry(key)?.value
    actual override fun isEmpty(): Boolean = size == 0

    actual override fun putAll(from: Map<out K, V>): Unit {
        for ((key, value) in from) {
            put(key, value)
        }
    }

    actual override fun remove(key: K): V? {
        val iterator = entries.iterator()
        while (iterator.hasNext()) {
            val entry = iterator.next()
            if (key == entry.key) {
                val value = entry.value
                iterator.remove()
                return value
            }
        }
        return null
    }

    /**
     * Associates the specified [value] with the specified [key] in the map.
     *
     * This method is redeclared as abstract, because it's not implemented in the base class,
     * so it must be always overridden in the concrete mutable collection implementation.
     *
     * @return the previous value associated with the key, or `null` if the key was not present in the map.
     */
    actual abstract override fun put(key: K, value: V): V?
}
