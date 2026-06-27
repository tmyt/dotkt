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
    actual override val keys: MutableSet<K>
        get() = TODO("clr binding should be implemented")
    actual override val size: Int
        get() = TODO("clr binding should be implemented")
    actual override val values: MutableCollection<V>
        get() = TODO("clr binding should be implemented")

    actual override fun clear(): Unit {
        TODO("clr binding should be implemented")
    }
    actual override fun containsKey(key: K): Boolean = TODO("clr binding should be implemented")
    actual override fun containsValue(value: V): Boolean = TODO("clr binding should be implemented")
    actual override fun get(key: K): V? = TODO("clr binding should be implemented")
    actual override fun isEmpty(): Boolean = TODO("clr binding should be implemented")
    actual override fun putAll(from: Map<out K, V>): Unit {
        TODO("clr binding should be implemented")
    }
    actual override fun remove(key: K): V? = TODO("clr binding should be implemented")

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
