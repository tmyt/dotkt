/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Provides a skeletal implementation of the [MutableCollection] interface.
 *
 * @param E the type of elements contained in the collection. The collection is invariant in its element type.
 */
// NOTE: the JVM actual gets the CONCRETE members (isEmpty/contains/...) for free by also extending
// java.util.AbstractCollection<E>. The CLR has no such base, so this actual must supply every member the expect
// declares — abstract ones stay abstract, concrete ones get a `TODO` stub (Phase 2 supplies real bodies / @Clr).
@SinceKotlin("1.1")
public actual abstract class AbstractMutableCollection<E> protected actual constructor() : MutableCollection<E> {
    actual abstract override val size: Int
    actual abstract override fun iterator(): MutableIterator<E>
    actual abstract override fun add(element: E): Boolean

    actual override fun isEmpty(): Boolean = size == 0

    actual override fun contains(element: E): Boolean {
        val iterator = iterator()
        while (iterator.hasNext()) {
            if (iterator.next() == element) return true
        }
        return false
    }

    actual override fun containsAll(elements: Collection<E>): Boolean = elements.all { contains(it) }

    actual override fun addAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) {
            if (add(element)) modified = true
        }
        return modified
    }

    actual override fun remove(element: E): Boolean {
        val iterator = iterator()
        while (iterator.hasNext()) {
            if (iterator.next() == element) {
                iterator.remove()
                return true
            }
        }
        return false
    }

    actual override fun removeAll(elements: Collection<E>): Boolean {
        var modified = false
        val iterator = iterator()
        while (iterator.hasNext()) {
            if (iterator.next() in elements) {
                iterator.remove()
                modified = true
            }
        }
        return modified
    }

    actual override fun retainAll(elements: Collection<E>): Boolean {
        var modified = false
        val iterator = iterator()
        while (iterator.hasNext()) {
            if (iterator.next() !in elements) {
                iterator.remove()
                modified = true
            }
        }
        return modified
    }

    actual override fun clear(): Unit {
        val iterator = iterator()
        while (iterator.hasNext()) {
            iterator.next()
            iterator.remove()
        }
    }
}
