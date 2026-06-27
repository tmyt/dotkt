/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Provides a skeletal implementation of the [MutableList] interface.
 *
 * @param E the type of elements contained in the list. The list is invariant in its element type.
 */
@SinceKotlin("1.1")
@Suppress("NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
public actual abstract class AbstractMutableList<E> protected actual constructor() : MutableList<E> {
    @SinceKotlin("2.0")
    protected actual var modCount: Int
        get() = TODO("clr binding should be implemented")
        set(value) { TODO("clr binding should be implemented") }

    @SinceKotlin("2.0")
    protected actual open fun removeRange(fromIndex: Int, toIndex: Int): Unit {
        TODO("clr binding should be implemented")
    }

    actual override fun isEmpty(): Boolean = TODO("clr binding should be implemented")
    actual override fun contains(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun containsAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun indexOf(element: E): Int = TODO("clr binding should be implemented")
    actual override fun lastIndexOf(element: E): Int = TODO("clr binding should be implemented")
    actual override fun iterator(): MutableIterator<E> = TODO("clr binding should be implemented")
    actual override fun add(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun remove(element: E): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun addAll(index: Int, elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun removeAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun retainAll(elements: Collection<E>): Boolean = TODO("clr binding should be implemented")
    actual override fun clear(): Unit {
        TODO("clr binding should be implemented")
    }
    actual override fun listIterator(): MutableListIterator<E> = TODO("clr binding should be implemented")
    actual override fun listIterator(index: Int): MutableListIterator<E> = TODO("clr binding should be implemented")
    actual override fun subList(fromIndex: Int, toIndex: Int): MutableList<E> = TODO("clr binding should be implemented")

    /**
     * Replaces the element at the specified position in this list with the specified element.
     *
     * This method is redeclared as abstract, because it's not implemented in the base class,
     * so it must be always overridden in the concrete mutable collection implementation.

     * @return the element previously at the specified position.
     */
    abstract override fun set(index: Int, element: E): E

    /**
     * Removes an element at the specified [index] from the list.
     *
     * This method is redeclared as abstract, because it's not implemented in the base class,
     * so it must be always overridden in the concrete mutable collection implementation.
     *
     * @return the element that has been removed.
     */
    abstract override fun removeAt(index: Int): E

    /**
     * Inserts an element into the list at the specified [index].
     *
     * This method is redeclared as abstract, because it's not implemented in the base class,
     * so it must be always overridden in the concrete mutable collection implementation.
     */
    abstract override fun add(index: Int, element: E)
}
