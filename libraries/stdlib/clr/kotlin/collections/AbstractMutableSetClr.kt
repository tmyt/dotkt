/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Provides a skeletal implementation of the [MutableSet] interface.
 *
 * @param E the type of elements contained in the set. The set is invariant in its element type.
 */
@SinceKotlin("1.1")
// The direct CLR face is retained; bulk mutation policy is shared with AbstractMutableCollection.
public actual abstract class AbstractMutableSet<E> protected actual constructor() : MutableSet<E> {
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 1)
    override fun hashCode(): Int = AbstractSet.unorderedHashCode(this)

    override fun toString(): String = joinToString(", ", "[", "]") {
        if (it === this) "(this Collection)" else it.toString()
    }

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

    actual override fun removeAll(elements: Collection<E>): Boolean = (this as MutableIterable<E>).removeAll { it in elements }

    actual override fun retainAll(elements: Collection<E>): Boolean = (this as MutableIterable<E>).retainAll { it in elements }

    actual override fun clear(): Unit {
        val iterator = iterator()
        while (iterator.hasNext()) {
            iterator.next()
            iterator.remove()
        }
    }

    /**
     * Adds the specified element to the set.
     *
     * This method is redeclared as abstract, because it's not implemented in the base class,
     * so it must be always overridden in the concrete mutable collection implementation.
     *
     * @return `true` if the element has been added, `false` if the element is already contained in the set.
     */
    actual abstract override fun add(element: E): Boolean
}
