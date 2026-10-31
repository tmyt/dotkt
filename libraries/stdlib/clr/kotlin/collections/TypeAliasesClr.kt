/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// CLR collection aliases and platform implementations. Null-capable map actuals live in HashMapsClr.kt.
// Native collection aliases bind to their BCL counterparts (List / HashSet). Members with a
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
    // NOTE: `add` binds to the BCL `List<T>.Add`, which returns VOID (Kotlin's `MutableList.add` returns Boolean — always
    // `true` for a list). So `add(element)` may only be used as a STATEMENT here (its result is absent on the CLR stack);
    // consuming its return (`if (add(e)) …`) emits a `brfalse` over an empty stack -> InvalidProgram. Every element is
    // always appended, so `modified` is simply whether the loop ran at all. (MutableSet.addAll below CAN consume the
    // boolean — HashSet<T>.Add returns bool.)
    // ArrayList IS `System.Collections.Generic.List<E>` (@ClrTypeAlias), so no TypeDef is emitted for it and it can
    // never carry the compiler-authored Kotlin slot interfaces — every receiver of this class takes the BCL default.
    // Forward to that single implementation instead of restating the same algorithm here.
    actual override fun addAll(elements: Collection<E>): Boolean = clrCollAddAll(this, elements)
    actual override fun addAll(index: Int, elements: Collection<E>): Boolean = clrListAddAllAt(this, index, elements)
    actual override fun removeAll(elements: Collection<E>): Boolean = clrCollRemoveAll(this, elements)
    actual override fun retainAll(elements: Collection<E>): Boolean = clrCollRetainAll(this, elements)
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

    init {
        AbstractList.checkRangeIndexes(fromIndex, toIndex, list.size)
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


// Native dictionary declarations retain their Kotlin map-only storage meaning for
// classifier/equality bindings. Kotlin's null-capable implementations are actual
// classes, not these native views; explicit projected constructors remain native.
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.Dictionary")
internal abstract class ClrNativeHashMap<K, V> : MutableMap<K, V>

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.OrderedDictionary")
internal abstract class ClrNativeOrderedMap<K, V> : MutableMap<K, V>


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

    // Native HashSet removal does not invalidate its cursor. Keep the native comparer/storage and adapt only the
    // Kotlin hasNext/next/remove protocol; an ordinary native collection's fallback remains a separate boundary.
    actual override fun iterator(): MutableIterator<E> {
        val source = iteratorOverEnumerable(this as ClrEnumerable<E>)
        return object : MutableIterator<E> {
            private var lastEl: E? = null
            private var hasLast = false
            override fun hasNext(): Boolean = try { source.hasNext() }
                catch (error: IllegalStateException) { throw ConcurrentModificationException(error.message, error) }
            override fun next(): E {
                if (!hasNext()) throw NoSuchElementException()
                val e = try { source.next() }
                    catch (error: IllegalStateException) { throw ConcurrentModificationException(error.message, error) }
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
    // HashSet IS `System.Collections.Generic.HashSet<E>` (@ClrTypeAlias) — no TypeDef, so never a slot implementer.
    // Same single-implementation forwarding as ArrayList above.
    actual override fun addAll(elements: Collection<E>): Boolean = clrCollAddAll(this, elements)
    actual override fun removeAll(elements: Collection<E>): Boolean = clrCollRemoveAll(this, elements)
    actual override fun retainAll(elements: Collection<E>): Boolean = clrCollRetainAll(this, elements)
    @kotlin.clr.ClrIntrinsic("Clear")
    actual override fun clear() { TODO("clr binding should be implemented") }
}


private class LinkedHashSetKey(val value: Any?) {
    override fun equals(other: Any?): Boolean = other is LinkedHashSetKey && value == other.value
    override fun hashCode(): Int = value?.hashCode() ?: 0
}

// #169: LinkedHashSet CONTRACTS insertion-order iteration (and setOf/mutableSetOf return LinkedHashSet, so they inherit
// it). .NET has NO insertion-ordered GENERIC set (`HashSet<E>` loses order after a removal), so — exactly as Kotlin/JVM
// backs LinkedHashSet with a LinkedHashMap — this is a REAL pure-Kotlin class backed by the (now insertion-ordered)
// [LinkedHashMap]. It DIRECTLY implements MutableSet<E> (= the aliased ICollection<E>), so CollectionBclSlotSynthesis
// fills the BCL ICollection slots (Contains/CopyTo/IsReadOnly + the Boolean->void Add bridge) and ilemit's reverse
// GetEnumerator bridge wraps iterator() — the SAME machinery AbstractMutableSet/ClrMatchGroupCollection rely on.
@SinceKotlin("1.1")
public actual class LinkedHashSet<E> : MutableSet<E> {
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 1)
    override fun hashCode(): Int = DotKt.Runtime.CompilerServices.structuralHash(this, 1)

    // Values retain the original elements in insertion order; these private keys use
    // the same Kotlin equality/hash semantics as set operations.
    private val map: LinkedHashMap<LinkedHashSetKey, E>

    public actual constructor() { map = LinkedHashMap() }
    public actual constructor(initialCapacity: Int) { map = LinkedHashMap(initialCapacity) }
    public actual constructor(initialCapacity: Int, loadFactor: Float) { map = LinkedHashMap(initialCapacity, loadFactor) }
    public actual constructor(elements: Collection<E>) { map = LinkedHashMap(); addAll(elements) }

    // From Set

    actual override val size: Int get() = map.size
    actual override fun isEmpty(): Boolean = map.isEmpty()
    actual override fun contains(element: E): Boolean = map.containsKey(LinkedHashSetKey(element))
    actual override fun containsAll(elements: Collection<E>): Boolean = clrCollContainsAll(this, elements)

    // From MutableSet

    actual override fun iterator(): MutableIterator<E> = map.values.iterator()
    actual override fun add(element: E): Boolean {
        val key = LinkedHashSetKey(element)
        if (map.containsKey(key)) return false
        map.put(key, element)
        return true
    }
    actual override fun remove(element: E): Boolean {
        val key = LinkedHashSetKey(element)
        if (!map.containsKey(key)) return false
        map.remove(key)
        return true
    }
    actual override fun addAll(elements: Collection<E>): Boolean {
        var modified = false
        for (element in elements) { if (add(element)) modified = true }
        return modified
    }
    actual override fun removeAll(elements: Collection<E>): Boolean = (this as MutableIterable<E>).removeAll { it in elements }
    actual override fun retainAll(elements: Collection<E>): Boolean = (this as MutableIterable<E>).retainAll { it in elements }
    actual override fun clear() { map.clear() }
}
