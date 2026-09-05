/*
 * CLR ref/runtime split: default implementations for Collection/List members that have NO equivalent on the substituted
 * BCL types (IReadOnlyCollection<T> / IReadOnlyList<T> only expose Count / this[i] / GetEnumerator). The backend routes a
 * member call `recv.isEmpty()` / `recv.contains(e)` / ... to these statics (mirrors the iterator() bridge). The bodies use
 * ONLY BCL-bound members (size->Count, get->get_Item, iterator->GetEnumerator) so they never recurse into a routed member.
 * See docs/design-clr-collection-binding.md.
 */
@file:Suppress("NOTHING_TO_INLINE", "UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.KotlinMutableCollectionSlots
import DotKt.Runtime.CompilerServices.KotlinMutableIteratorSlots
import DotKt.Runtime.CompilerServices.KotlinMutableListSlots
import DotKt.Runtime.CompilerServices.KotlinCollectionDefaultSlots
import DotKt.Runtime.CompilerServices.KotlinListDefaultSlots
import DotKt.Runtime.CompilerServices.listIteratorHasNextErased
import DotKt.Runtime.CompilerServices.listIteratorHasPreviousErased
import DotKt.Runtime.CompilerServices.listIteratorNextErased
import DotKt.Runtime.CompilerServices.listIteratorNextIndexErased
import DotKt.Runtime.CompilerServices.listIteratorPreviousErased
import DotKt.Runtime.CompilerServices.listIteratorPreviousIndexErased
import DotKt.Runtime.CompilerServices.mutableCollectionAddErased
import DotKt.Runtime.CompilerServices.mutableCollectionCountErased
import DotKt.Runtime.CompilerServices.mutableCollectionRemoveErased
import DotKt.Runtime.CompilerServices.mutableCollectionReplaceErased
import DotKt.Runtime.CompilerServices.mutableListGetErased
import DotKt.Runtime.CompilerServices.mutableListInsertErased
import DotKt.Runtime.CompilerServices.mutableListIteratorAddErased
import DotKt.Runtime.CompilerServices.mutableListIteratorSetErased
import DotKt.Runtime.CompilerServices.mutableListRemoveAtErased
import DotKt.Runtime.CompilerServices.mutableListSetErased
import DotKt.Runtime.CompilerServices.mutableIteratorHasNextErased
import DotKt.Runtime.CompilerServices.mutableIteratorNextErased
import DotKt.Runtime.CompilerServices.mutableIteratorRemoveErased
import DotKt.Runtime.CompilerServices.projectedCollectionCountErased
import DotKt.Runtime.CompilerServices.projectedListGetErased

/** Variance-independent mutable-list face used after `MutableIterable<out T>` has widened its element type. */
@kotlin.clr.ClrTypeAlias("System.Collections.IList")
private interface ClrRawMutableList {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Count") val count: Int
    @kotlin.clr.ClrIntrinsic("get_Item") fun get(index: Int): Any?
    @kotlin.clr.ClrIntrinsic("RemoveAt") fun removeAt(index: Int): Unit
}

private class ClrErasedMutableIteratorAdapter<T>(private val iterator: Any) : MutableIterator<T> {
    override fun hasNext(): Boolean = mutableIteratorHasNextErased(iterator)
    override fun next(): T = mutableIteratorNextErased(iterator) as T
    override fun remove() = mutableIteratorRemoveErased(iterator)
}

private class ClrProjectedIterator<T>(private val iterator: Iterator<Any?>) : Iterator<T> {
    override fun hasNext(): Boolean = iterator.hasNext()
    override fun next(): T = iterator.next() as T
}

public fun <T> clrProjectedIterator(iterable: Any): Iterator<T> =
    ClrProjectedIterator(iteratorOverRawEnumerable(iterable))

// A projected collection can legally cross into a covariant Collection<T> slot even when its actual element is a
// value type. CLR variance cannot express that edge (`IReadOnlyCollection<Int32>` is not an
// `IReadOnlyCollection<Object>`), so preserve the original receiver whenever it already inhabits the requested
// closed face and otherwise expose a live, read-only view over its non-generic enumerable face.
private class ClrProjectedCollectionView<T>(private val source: Any) : Collection<T> {
    override val size: Int get() = projectedCollectionCountErased(source)

    override fun isEmpty(): Boolean = clrProjectedCollIsEmpty<Any?>(source)
    override fun contains(element: T): Boolean = clrProjectedCollContains(source, element)
    override fun containsAll(elements: Collection<T>): Boolean = clrProjectedCollContainsAll(source, elements)
    override fun iterator(): Iterator<T> = clrProjectedIterator(source)
}

public fun <T> clrProjectedCollectionView(source: Any): Collection<T> =
    (source as? Collection<T>) ?: ClrProjectedCollectionView(source)

public fun <T> clrCollIsEmpty(c: Collection<T>): Boolean = c.size == 0

/** Raw ICollection Add — VOID on the BCL (the Kotlin changed-Boolean wrapper is [clrCollAdd]). */
@kotlin.clr.ClrIntrinsic("Add")
public fun <T> MutableCollection<T>.clrCollNativeAdd(element: T): Unit = TODO("clr binding should be implemented")

/**
 * `MutableCollection.add`: Kotlin returns whether the collection CHANGED; `ICollection<T>.Add` is void
 * (a set silently ignores a duplicate). Synthesized as Add + size compare — true for a list always,
 * duplicate-aware for a set.
 */
public fun <T> clrCollAdd(c: MutableCollection<T>, element: T): Boolean {
    val before = c.size
    c.clrCollNativeAdd(element)
    return c.size != before
}

/** Projected `MutableCollection.add`; the receiver's exact invariant collection element is known only at runtime. */
public fun <T> clrProjectedCollAdd(c: Any, element: T): Boolean = mutableCollectionAddErased(c, element)

private fun <T> clrProjectedCollSnapshot(source: Any): ArrayList<T> {
    val out = ArrayList<T>()
    val iterator = iteratorOverRawEnumerable(source)
    while (iterator.hasNext()) out.add(iterator.next() as T)
    return out
}

public fun <T> clrProjectedCollIsEmpty(c: Any): Boolean {
    val slots = c as? KotlinCollectionDefaultSlots
    return slots?.dotktIsEmpty() ?: (projectedCollectionCountErased(c) == 0)
}

public fun <T> clrProjectedCollContains(c: Any, element: T): Boolean {
    val slots = c as? KotlinCollectionDefaultSlots
    if (slots != null) return slots.dotktContains(element)
    val iterator = iteratorOverRawEnumerable(c)
    while (iterator.hasNext()) if (iterator.next() == element) return true
    return false
}

public fun <T> clrProjectedCollContainsAll(c: Any, elements: Collection<T>): Boolean {
    val slots = c as? KotlinCollectionDefaultSlots
    if (slots != null) return slots.dotktContainsAll(elements)
    for (element in elements) if (!clrProjectedCollContains(c, element)) return false
    return true
}

public fun <T> clrProjectedCollAddAll(c: Any, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktAddAll(elements)
    var changed = false
    for (element in clrCollSnapshot(elements)) if (mutableCollectionAddErased(c, element)) changed = true
    return changed
}

public fun <T> clrProjectedCollRemoveAll(c: Any, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktRemoveAll(elements)
    var changed = false
    for (element in clrCollSnapshot(elements)) while (mutableCollectionRemoveErased(c, element)) changed = true
    return changed
}

public fun <T> clrProjectedCollRetainAll(c: Any, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktRetainAll(elements)
    val current = clrProjectedCollSnapshot<Any?>(c)
    var changed = false
    for (element in current) if (!clrProjectedCollContains(elements, element)) {
        while (mutableCollectionRemoveErased(c, element)) changed = true
    }
    return changed
}

public fun <T> clrProjectedListAddAllAt(list: Any, index: Int, elements: Collection<T>): Boolean {
    val slots = list as? KotlinMutableListSlots
    if (slots != null) return slots.dotktAddAllAt(index, elements)
    var at = index
    var changed = false
    for (element in clrCollSnapshot(elements)) {
        mutableListInsertErased(list, at, element)
        at++
        changed = true
    }
    return changed
}

public fun <T> clrProjectedListSet(list: Any, index: Int, element: T): Any? {
    val old = mutableListGetErased(list, index)
    mutableListSetErased(list, index, element)
    return old
}

public fun <T> clrProjectedListRemoveAt(list: Any, index: Int): Any? {
    val old = mutableListGetErased(list, index)
    mutableListRemoveAtErased(list, index)
    return old
}

public fun <T> clrProjectedListIndexOf(list: Any, element: T): Int {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return slots.dotktIndexOf(element)
    var index = 0
    val iterator = iteratorOverRawEnumerable(list)
    while (iterator.hasNext()) {
        if (iterator.next() == element) return index
        index++
    }
    return -1
}

public fun <T> clrProjectedListLastIndexOf(list: Any, element: T): Int {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return slots.dotktLastIndexOf(element)
    var found = -1
    var index = 0
    val iterator = iteratorOverRawEnumerable(list)
    while (iterator.hasNext()) {
        if (iterator.next() == element) found = index
        index++
    }
    return found
}

// ---- Kotlin-only mutation members: capability dispatch, then the BCL default -----------------------------------
//
// Mutable `iterator` / `addAll` / `removeAll` / `retainAll` / `addAll(index, …)` are VIRTUAL Kotlin members of
// `MutableIterable` / `MutableCollection` / `MutableList` with no matching slot on their BCL operational faces.
// The dispatchers below are the ONE place where the two legitimate receiver categories are reconciled:
//
//   * a Kotlin implementer carries the matching compiler-authored [KotlinMutableIteratorSlots] /
//     [KotlinMutableCollectionSlots] / [KotlinMutableListSlots] interface, so its (possibly overridden) member is
//     reached through a real virtual slot;
//   * a BCL-backed receiver has no Kotlin body, so the default below runs, written ONLY over members that do have a
//     physical slot (`Remove`, `Insert`, `GetEnumerator`) plus the already-routed [clrCollAdd] / [clrCollContains].
//
// The capability test is on a NON-generic interface on purpose — see the notes on the slot interfaces:
// a constructed test would be defeated by an element type erased to `System.Object` at the call site and would then
// silently skip a user override.
//
// Every default SNAPSHOTS the collection it iterates before mutating, so the self-aliasing forms are defined rather
// than throwing a concurrent-modification error out of a BCL enumerator: `c.removeAll(c)` empties `c`,
// `c.retainAll(c)` leaves it unchanged, and `c.addAll(c)` appends the original contents once.

/** Snapshot any Kotlin collection into a private list, so a mutation of the receiver cannot invalidate iteration. */
private fun <T> clrCollSnapshot(source: Collection<T>): ArrayList<T> {
    val out = ArrayList<T>()
    for (e in source) out.add(e)
    return out
}

/** `MutableCollection.addAll`: Kotlin slot if the receiver has one, else element-wise [clrCollAdd]. */
public fun <T> clrCollAddAll(c: MutableCollection<T>, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktAddAll(elements)
    var changed = false
    for (e in clrCollSnapshot(elements)) if (clrCollAdd(c, e)) changed = true
    return changed
}

/**
 * `MutableCollection.removeAll`: Kotlin slot if the receiver has one, else remove EVERY occurrence of each named
 * element (Kotlin's contract is "removes all of this collection's elements that are also contained in [elements]",
 * so a duplicate in the receiver must not survive).
 */
public fun <T> clrCollRemoveAll(c: MutableCollection<T>, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktRemoveAll(elements)
    var changed = false
    for (e in clrCollSnapshot(elements)) while (c.remove(e)) changed = true
    return changed
}

/**
 * `MutableCollection.retainAll`: Kotlin slot if the receiver has one, else drop every element of the receiver that
 * is not contained in [elements]. Both sides are snapshotted first so `c.retainAll(c)` is a well-defined no-op.
 */
public fun <T> clrCollRetainAll(c: MutableCollection<T>, elements: Collection<T>): Boolean {
    val slots = c as? KotlinMutableCollectionSlots
    if (slots != null) return slots.dotktRetainAll(elements)
    val keep = clrCollSnapshot(elements)
    var changed = false
    for (e in clrCollSnapshot(c)) if (!clrCollContains(keep, e)) { c.remove(e); changed = true }
    return changed
}

/** `MutableList.addAll(index, elements)`: Kotlin slot if the receiver has one, else element-wise `Insert`. */
public fun <T> clrListAddAllAt(list: MutableList<T>, index: Int, elements: Collection<T>): Boolean {
    val slots = list as? KotlinMutableListSlots
    if (slots != null) return slots.dotktAddAllAt(index, elements)
    var at = index
    var changed = false
    for (e in clrCollSnapshot(elements)) { list.add(at, e); at++; changed = true }
    return changed
}

public fun <T> clrCollContains(c: Collection<T>, element: T): Boolean {
    for (x in c) if (x == element) return true
    return false
}

public fun <T> clrCollContainsAll(c: Collection<T>, elements: Collection<T>): Boolean {
    for (e in elements) if (!clrCollContains(c, e)) return false
    return true
}

// Kotlin's collection toString is `[a, b, c]` (AbstractCollection.toString). The substituted BCL list (.NET List<T>)
// uses .NET's default `System.Collections.Generic.List`1[...]` instead, so the backend routes `coll.toString()` and
// `println(coll)` here.
public fun <T> clrCollToString(c: Collection<T>): String {
    val sb = StringBuilder()
    sb.append("[")
    var first = true
    for (x in c) {
        if (!first) sb.append(", ")
        first = false
        sb.append(clrElemToString(x))   // recurse: a nested collection/map renders Kotlin-style, not .NET's raw name (N7)
    }
    sb.append("]")
    return sb.toString()
}

// `MutableList.set(i,e)` / `removeAt(i)` RETURN the previous/removed element in Kotlin, but `IList<T>.set_Item` /
// `RemoveAt` are VOID on the BCL — binding the calls directly to the void slots underflows the stack when the return is
// consumed (`val old = list.set(i,e)` -> InvalidProgramException). These wrappers read the old element first (get_Item),
// then perform the void mutation, and return it — the list mirror of ClrMapDefaults.clrMapPut. The backend should route
// `MutableList.set`/`removeAt` here (a pre-intrinsic-rule override, like MutableCollection.add -> clrCollAdd).
/** Raw IList set_Item — VOID on the BCL (the previous-value-returning wrapper is [clrListSet]). */
@kotlin.clr.ClrIntrinsic("set_Item")
public fun <T> MutableList<T>.clrListNativeSet(index: Int, element: T): Unit = TODO("clr binding should be implemented")

/** Raw IList RemoveAt — VOID on the BCL (the removed-value-returning wrapper is [clrListRemoveAt]). */
@kotlin.clr.ClrIntrinsic("RemoveAt")
public fun <T> MutableList<T>.clrListNativeRemoveAt(index: Int): Unit = TODO("clr binding should be implemented")

public fun <T> clrListSet(list: MutableList<T>, index: Int, element: T): T {
    val old = list[index]
    list.clrListNativeSet(index, element)
    return old
}

public fun <T> clrListRemoveAt(list: MutableList<T>, index: Int): T {
    val old = list[index]
    list.clrListNativeRemoveAt(index)
    return old
}

public fun <T> clrListIndexOf(list: List<T>, element: T): Int {
    var i = 0
    for (x in list) { if (x == element) return i; i++ }
    return -1
}

public fun <T> clrListLastIndexOf(list: List<T>, element: T): Int {
    var idx = -1
    var i = 0
    for (x in list) { if (x == element) idx = i; i++ }
    return idx
}

// A ListIterator over a BCL-backed List (uses only size/get). ListIterator is a pure-Kotlin type (NOT @Clr-bound), so
// this class is emitted normally — no reverse-direction (C3b) GetEnumerator obligation.
private class ClrListIterator<T>(private val list: List<T>, index: Int) : ListIterator<T> {
    private var cursor = index
    override fun hasNext(): Boolean = cursor < list.size
    override fun next(): T { val v = list[cursor]; cursor++; return v }
    override fun hasPrevious(): Boolean = cursor > 0
    override fun previous(): T { cursor--; return list[cursor] }
    override fun nextIndex(): Int = cursor
    override fun previousIndex(): Int = cursor - 1
}

public fun <T> clrListListIterator(list: List<T>, index: Int): ListIterator<T> = ClrListIterator(list, index)

// MutableList aliases IList<T>, whose IEnumerable<T>.GetEnumerator surface cannot represent Kotlin's
// remove/set/add iterator contract. Keep that semantic adapter here, beside the other Kotlin<->CLR list defaults,
// and build it only from MutableList operations that bir2cir already maps to IList<T>.
private class ClrMutableListIterator<T>(
    private val list: MutableList<T>,
    index: Int
) : MutableListIterator<T> {
    private var cursor = index
    private var last = -1

    init {
        if (index < 0 || index > list.size) throw IndexOutOfBoundsException()
    }

    override fun hasNext(): Boolean = cursor < list.size
    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        last = cursor
        cursor++
        return list[last]
    }
    override fun hasPrevious(): Boolean = cursor > 0
    override fun previous(): T {
        if (!hasPrevious()) throw NoSuchElementException()
        cursor--
        last = cursor
        return list[last]
    }
    override fun nextIndex(): Int = cursor
    override fun previousIndex(): Int = cursor - 1
    override fun remove() {
        if (last < 0) throw IllegalStateException()
        list.removeAt(last)
        if (last < cursor) cursor--
        last = -1
    }
    override fun set(element: T) {
        if (last < 0) throw IllegalStateException()
        list[last] = element
    }
    override fun add(element: T) {
        list.add(cursor, element)
        cursor++
        last = -1
    }
}

private class ClrRawMutableListIterator<T>(private val list: ClrRawMutableList) : MutableIterator<T> {
    private var cursor = 0
    private var last = -1

    override fun hasNext(): Boolean = cursor < list.count
    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        val value = list.get(cursor) as T
        last = cursor
        cursor++
        return value
    }
    override fun remove() {
        if (last < 0) throw IllegalStateException("next() has not been called")
        list.removeAt(last)
        cursor--
        last = -1
    }
}

// MutableIterable aliases covariant IEnumerable<T>, whose GetEnumerator return cannot represent Kotlin's
// remove-capable MutableIterator<T>. Kotlin implementers carry a compiler-authored non-generic slot preserving their
// exact override. BCL lists use the non-generic indexed face, so a widened element type remains valid. Other BCL
// mutable collections snapshot enumeration; ordinary removals invoke their exact closed ICollection<T> view. If an
// earlier equal occurrence would make Remove(value) target the wrong element, rebuild the collection from the snapshot
// without the exact last-returned occurrence.
private class ClrErasedMutableCollectionIterator<T>(
    private val collection: Any,
    private val snapshot: MutableList<T>,
) : MutableIterator<T> {
    private var index = 0
    private var lastIndex = -1

    override fun hasNext(): Boolean = index < snapshot.size
    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        val value = snapshot[index]
        lastIndex = index
        index++
        return value
    }
    override fun remove() {
        if (lastIndex < 0) throw IllegalStateException("next() has not been called")
        val value = snapshot[lastIndex]
        var earlierEqual = false
        var at = 0
        while (at < lastIndex) {
            if (snapshot[at] == value) { earlierEqual = true; break }
            at++
        }
        snapshot.removeAt(lastIndex)
        index--
        if (earlierEqual) {
            val replacement = arrayOfNulls<Any?>(snapshot.size)
            at = 0
            while (at < snapshot.size) { replacement[at] = snapshot[at]; at++ }
            mutableCollectionReplaceErased(collection, replacement)
        } else {
            mutableCollectionRemoveErased(collection, value)
        }
        lastIndex = -1
    }
}

public fun <T> clrMutableIterator(iterable: Any): MutableIterator<T> {
    val slots = iterable as? KotlinMutableIteratorSlots
    if (slots != null) return ClrErasedMutableIteratorAdapter(slots.dotktIterator())
    val list = iterable as? ClrRawMutableList
    if (list != null) return ClrRawMutableListIterator(list)
    val snapshot = ArrayList<T>()
    val source = iteratorOverRawEnumerable(iterable)
    while (source.hasNext()) snapshot.add(source.next() as T)
    return ClrErasedMutableCollectionIterator(iterable, snapshot)
}

/** Star-projected twin: both capability tests and BCL fallbacks are independent of the erased element type. */
public fun clrMutableIteratorErased(iterable: Any): MutableIterator<Any?> {
    val slots = iterable as? KotlinMutableIteratorSlots
    if (slots != null) return ClrErasedMutableIteratorAdapter<Any?>(slots.dotktIterator())
    val list = iterable as? ClrRawMutableList
    if (list != null) return ClrRawMutableListIterator(list)
    val snapshot = ArrayList<Any?>()
    val source = iteratorOverRawEnumerable(iterable)
    while (source.hasNext()) snapshot.add(source.next())
    return ClrErasedMutableCollectionIterator(iterable, snapshot)
}

public fun <T> clrMutableListListIterator(list: MutableList<T>, index: Int): MutableListIterator<T> =
    ClrMutableListIterator(list, index)

private class ClrProjectedMutableListIterator<T>(private val list: Any, index: Int) : MutableListIterator<T> {
    private var cursor = index
    private var last = -1

    init {
        if (index < 0 || index > mutableCollectionCountErased(list)) throw IndexOutOfBoundsException()
    }

    override fun hasNext(): Boolean = cursor < mutableCollectionCountErased(list)
    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        last = cursor
        cursor++
        return mutableListGetErased(list, last) as T
    }
    override fun hasPrevious(): Boolean = cursor > 0
    override fun previous(): T {
        if (!hasPrevious()) throw NoSuchElementException()
        cursor--
        last = cursor
        return mutableListGetErased(list, last) as T
    }
    override fun nextIndex(): Int = cursor
    override fun previousIndex(): Int = cursor - 1
    override fun remove() {
        if (last < 0) throw IllegalStateException()
        mutableListRemoveAtErased(list, last)
        if (last < cursor) cursor--
        last = -1
    }
    override fun set(element: T) {
        if (last < 0) throw IllegalStateException()
        mutableListSetErased(list, last, element)
    }
    override fun add(element: T) {
        mutableListInsertErased(list, cursor, element)
        cursor++
        last = -1
    }
}

private class ClrErasedListIteratorAdapter<T>(private val iterator: Any) : ListIterator<T> {
    override fun hasNext(): Boolean = listIteratorHasNextErased(iterator)
    override fun next(): T = listIteratorNextErased(iterator) as T
    override fun hasPrevious(): Boolean = listIteratorHasPreviousErased(iterator)
    override fun previous(): T = listIteratorPreviousErased(iterator) as T
    override fun nextIndex(): Int = listIteratorNextIndexErased(iterator)
    override fun previousIndex(): Int = listIteratorPreviousIndexErased(iterator)
}

private class ClrErasedMutableListIteratorAdapter<T>(private val iterator: Any) : MutableListIterator<T> {
    override fun hasNext(): Boolean = listIteratorHasNextErased(iterator)
    override fun next(): T = listIteratorNextErased(iterator) as T
    override fun hasPrevious(): Boolean = listIteratorHasPreviousErased(iterator)
    override fun previous(): T = listIteratorPreviousErased(iterator) as T
    override fun nextIndex(): Int = listIteratorNextIndexErased(iterator)
    override fun previousIndex(): Int = listIteratorPreviousIndexErased(iterator)
    override fun remove() = mutableIteratorRemoveErased(iterator)
    override fun set(element: T) = mutableListIteratorSetErased(iterator, element)
    override fun add(element: T) = mutableListIteratorAddErased(iterator, element)
}

public fun <T> clrProjectedMutableListIterator(list: Any): MutableListIterator<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return ClrErasedMutableListIteratorAdapter(slots.dotktListIterator())
    return ClrProjectedMutableListIterator(list, 0)
}

public fun <T> clrProjectedMutableListListIterator(list: Any, index: Int): MutableListIterator<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return ClrErasedMutableListIteratorAdapter(slots.dotktListIteratorAt(index))
    return ClrProjectedMutableListIterator(list, index)
}

public fun <T> clrProjectedListIterator(list: Any): ListIterator<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return ClrErasedListIteratorAdapter(slots.dotktListIterator())
    return ClrProjectedListView<T>(list, 0, projectedCollectionCountErased(list)).listIterator()
}

public fun <T> clrProjectedListListIterator(list: Any, index: Int): ListIterator<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) return ClrErasedListIteratorAdapter(slots.dotktListIteratorAt(index))
    return ClrProjectedListView<T>(list, 0, projectedCollectionCountErased(list)).listIterator(index)
}

// subList -> a live read-only view. ClrSubList implements List (@Clr) so it gets get_Count/get_Item (C3a) + a generated
// GetEnumerator (the reverse bridge). It only needs size/get; the non-BCL members route to the helpers above.
private class ClrSubList<T>(private val backing: List<T>, private val fromIndex: Int, private val toIndex: Int) : List<T> {
    override val size: Int get() = toIndex - fromIndex
    override fun get(index: Int): T = backing[fromIndex + index]
    override fun isEmpty(): Boolean = clrCollIsEmpty(this)
    override fun contains(element: T): Boolean = clrCollContains(this, element)
    override fun containsAll(elements: Collection<T>): Boolean = clrCollContainsAll(this, elements)
    override fun indexOf(element: T): Int = clrListIndexOf(this, element)
    override fun lastIndexOf(element: T): Int = clrListLastIndexOf(this, element)
    override fun iterator(): Iterator<T> = listIterator()
    override fun listIterator(): ListIterator<T> = clrListListIterator(this, 0)
    override fun listIterator(index: Int): ListIterator<T> = clrListListIterator(this, index)
    override fun subList(fromIndex: Int, toIndex: Int): List<T> = ClrSubList(this, fromIndex, toIndex)
}

public fun <T> clrListSubList(list: List<T>, fromIndex: Int, toIndex: Int): List<T> = ClrSubList(list, fromIndex, toIndex)

private fun clrCheckSubListBounds(size: Int, fromIndex: Int, toIndex: Int) {
    if (fromIndex < 0 || toIndex > size || fromIndex > toIndex) throw IndexOutOfBoundsException()
}

private class ClrProjectedListView<T>(
    private val backing: Any,
    private val fromIndex: Int,
    private val toIndex: Int,
) : List<T> {
    init { clrCheckSubListBounds(projectedCollectionCountErased(backing), fromIndex, toIndex) }

    override val size: Int get() = toIndex - fromIndex
    override fun get(index: Int): T {
        if (index < 0 || index >= size) throw IndexOutOfBoundsException()
        return projectedListGetErased(backing, fromIndex + index) as T
    }
    override fun isEmpty(): Boolean = size == 0
    override fun contains(element: T): Boolean = indexOf(element) >= 0
    override fun containsAll(elements: Collection<T>): Boolean = clrCollContainsAll(this, elements)
    override fun indexOf(element: T): Int = clrListIndexOf(this, element)
    override fun lastIndexOf(element: T): Int = clrListLastIndexOf(this, element)
    override fun iterator(): Iterator<T> = listIterator()
    override fun listIterator(): ListIterator<T> = ClrListIterator(this, 0)
    override fun listIterator(index: Int): ListIterator<T> = ClrListIterator(this, index)
    override fun subList(fromIndex: Int, toIndex: Int): List<T> {
        clrCheckSubListBounds(size, fromIndex, toIndex)
        return ClrProjectedListView(backing, this.fromIndex + fromIndex, this.fromIndex + toIndex)
    }
}

public fun <T> clrProjectedListSubList(list: Any, fromIndex: Int, toIndex: Int): List<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) {
        val result = slots.dotktSubList(fromIndex, toIndex)
        return (result as? List<T>)
            ?: ClrProjectedListView(result, 0, projectedCollectionCountErased(result))
    }
    return ClrProjectedListView(list, fromIndex, toIndex)
}

private class ClrProjectedMutableSubList<T>(
    private val backing: Any,
    private val start: Int,
    private var end: Int,
    private val parent: ClrProjectedMutableSubList<T>? = null,
) : MutableList<T> {
    init { clrCheckSubListBounds(mutableCollectionCountErased(backing), start, end) }

    override val size: Int get() = end - start

    private fun checkElement(index: Int) {
        if (index < 0 || index >= size) throw IndexOutOfBoundsException()
    }

    private fun checkPosition(index: Int) {
        if (index < 0 || index > size) throw IndexOutOfBoundsException()
    }

    private fun resized(delta: Int) {
        end += delta
        parent?.resized(delta)
    }

    override fun get(index: Int): T {
        checkElement(index)
        return mutableListGetErased(backing, start + index) as T
    }
    override fun set(index: Int, element: T): T {
        checkElement(index)
        return clrProjectedListSet<T>(backing, start + index, element) as T
    }
    override fun add(element: T): Boolean { add(size, element); return true }
    override fun add(index: Int, element: T) {
        checkPosition(index)
        mutableListInsertErased(backing, start + index, element)
        resized(1)
    }
    override fun addAll(elements: Collection<T>): Boolean = addAll(size, elements)
    override fun addAll(index: Int, elements: Collection<T>): Boolean {
        checkPosition(index)
        val added = elements.size
        if (!clrProjectedListAddAllAt(backing, start + index, elements)) return false
        resized(added)
        return true
    }
    override fun removeAt(index: Int): T {
        checkElement(index)
        val removed = clrProjectedListRemoveAt<T>(backing, start + index) as T
        resized(-1)
        return removed
    }
    override fun remove(element: T): Boolean {
        val index = indexOf(element)
        if (index < 0) return false
        removeAt(index)
        return true
    }
    override fun removeAll(elements: Collection<T>): Boolean {
        var changed = false
        var index = size - 1
        while (index >= 0) {
            if (elements.contains(get(index))) { removeAt(index); changed = true }
            index--
        }
        return changed
    }
    override fun retainAll(elements: Collection<T>): Boolean {
        var changed = false
        var index = size - 1
        while (index >= 0) {
            if (!elements.contains(get(index))) { removeAt(index); changed = true }
            index--
        }
        return changed
    }
    override fun clear() {
        var index = size - 1
        while (index >= 0) { removeAt(index); index-- }
    }
    override fun isEmpty(): Boolean = size == 0
    override fun contains(element: T): Boolean = indexOf(element) >= 0
    override fun containsAll(elements: Collection<T>): Boolean = clrCollContainsAll(this, elements)
    override fun indexOf(element: T): Int = clrListIndexOf(this, element)
    override fun lastIndexOf(element: T): Int = clrListLastIndexOf(this, element)
    override fun iterator(): MutableIterator<T> = listIterator()
    override fun listIterator(): MutableListIterator<T> = ClrMutableListIterator(this, 0)
    override fun listIterator(index: Int): MutableListIterator<T> = ClrMutableListIterator(this, index)
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<T> {
        clrCheckSubListBounds(size, fromIndex, toIndex)
        return ClrProjectedMutableSubList(backing, start + fromIndex, start + toIndex, this)
    }
}

public fun <T> clrProjectedMutableListSubList(list: Any, fromIndex: Int, toIndex: Int): MutableList<T> {
    val slots = list as? KotlinListDefaultSlots
    if (slots != null) {
        val result = slots.dotktSubList(fromIndex, toIndex)
        return (result as? MutableList<T>)
            ?: ClrProjectedMutableSubList(result, 0, mutableCollectionCountErased(result))
    }
    return ClrProjectedMutableSubList(list, fromIndex, toIndex)
}

// ---- Structural equality (Kotlin `==` on collections is structural; the substituted BCL types use REFERENCE ----
// Object.Equals, so the backend routes a collection `==` here). Null-safe: the backend passes the raw operands.

/** Kotlin structural List/ordered-collection equality: same size, elementwise-equal IN ORDER. */
public fun <T> clrCollStructEquals(a: Collection<T>?, b: Collection<T>?): Boolean {
    if (a === b) return true
    if (a == null || b == null) return false
    if (a.size != b.size) return false
    val ai = a.iterator()
    val bi = b.iterator()
    while (ai.hasNext()) {
        if (ai.next() != bi.next()) return false
    }
    return true
}

/** Kotlin structural Set equality: same size and each element mutually contained (unordered). */
public fun <T> clrSetStructEquals(a: Collection<T>?, b: Collection<T>?): Boolean {
    if (a === b) return true
    if (a == null || b == null) return false
    if (a.size != b.size) return false
    return clrCollContainsAll(a, b)
}
