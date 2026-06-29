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
    private var _modCount: Int = 0

    @SinceKotlin("2.0")
    protected actual var modCount: Int
        get() = _modCount
        set(value) { _modCount = value }

    @SinceKotlin("2.0")
    protected actual open fun removeRange(fromIndex: Int, toIndex: Int): Unit {
        val iterator = listIterator(fromIndex)
        repeat(toIndex - fromIndex) {
            iterator.next()
            iterator.remove()
        }
    }

    actual override fun isEmpty(): Boolean = size == 0
    actual override fun contains(element: E): Boolean = indexOf(element) >= 0
    actual override fun containsAll(elements: Collection<E>): Boolean = elements.all { contains(it) }

    actual override fun indexOf(element: E): Int {
        for (index in 0 until size) {
            if (get(index) == element) return index
        }
        return -1
    }

    actual override fun lastIndexOf(element: E): Int {
        for (index in size - 1 downTo 0) {
            if (get(index) == element) return index
        }
        return -1
    }

    actual override fun iterator(): MutableIterator<E> = IteratorImpl()

    actual override fun add(element: E): Boolean {
        add(size, element)
        return true
    }

    actual override fun remove(element: E): Boolean {
        val index = indexOf(element)
        if (index < 0) return false
        removeAt(index)
        return true
    }

    actual override fun addAll(elements: Collection<E>): Boolean = addAll(size, elements)

    actual override fun addAll(index: Int, elements: Collection<E>): Boolean {
        AbstractList.checkPositionIndex(index, size)
        var insertIndex = index
        var changed = false
        for (e in elements) {
            add(insertIndex++, e)
            changed = true
        }
        return changed
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
        removeRange(0, size)
    }

    actual override fun listIterator(): MutableListIterator<E> = ListIteratorImpl(0)
    actual override fun listIterator(index: Int): MutableListIterator<E> = ListIteratorImpl(index)
    actual override fun subList(fromIndex: Int, toIndex: Int): MutableList<E> = SubList(this, fromIndex, toIndex)

    private open inner class IteratorImpl : MutableIterator<E> {
        /** the index of the item that will be returned on the next call to [next]`()` */
        protected var index = 0
        /** the index of the item that was returned on the previous call to [next]`()` or [MutableListIterator.previous]`()` */
        protected var last = -1

        override fun hasNext(): Boolean = index < size

        override fun next(): E {
            if (!hasNext()) throw NoSuchElementException()
            last = index++
            return get(last)
        }

        override fun remove() {
            check(last != -1) { "Call next() or previous() before removing element from the iterator." }
            removeAt(last)
            index = last
            last = -1
        }
    }

    private inner class ListIteratorImpl(index: Int) : IteratorImpl(), MutableListIterator<E> {
        init {
            AbstractList.checkPositionIndex(index, this@AbstractMutableList.size)
            this.index = index
        }

        override fun hasPrevious(): Boolean = index > 0
        override fun nextIndex(): Int = index
        override fun previousIndex(): Int = index - 1

        override fun previous(): E {
            if (!hasPrevious()) throw NoSuchElementException()
            last = --index
            return get(last)
        }

        override fun set(element: E) {
            check(last != -1) { "Call next() or previous() before updating element value with the iterator." }
            this@AbstractMutableList.set(last, element)
        }

        override fun add(element: E) {
            this@AbstractMutableList.add(index, element)
            index++
            last = -1
        }
    }

    private class SubList<E>(
        private val list: AbstractMutableList<E>,
        private val fromIndex: Int,
        toIndex: Int
    ) : AbstractMutableList<E>(), RandomAccess {
        private var _size: Int = 0

        init {
            AbstractList.checkRangeIndexes(fromIndex, toIndex, list.size)
            this._size = toIndex - fromIndex
        }

        override fun add(index: Int, element: E) {
            AbstractList.checkPositionIndex(index, _size)
            list.add(fromIndex + index, element)
            _size++
        }

        override fun get(index: Int): E {
            AbstractList.checkElementIndex(index, _size)
            return list[fromIndex + index]
        }

        override fun removeAt(index: Int): E {
            AbstractList.checkElementIndex(index, _size)
            val result = list.removeAt(fromIndex + index)
            _size--
            return result
        }

        override fun set(index: Int, element: E): E {
            AbstractList.checkElementIndex(index, _size)
            return list.set(fromIndex + index, element)
        }

        override val size: Int get() = _size
    }

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
