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
import DotKt.Runtime.CompilerServices.mutableCollectionRemoveErased
import DotKt.Runtime.CompilerServices.mutableCollectionReplaceErased
import DotKt.Runtime.CompilerServices.mutableIteratorHasNextErased
import DotKt.Runtime.CompilerServices.mutableIteratorNextErased
import DotKt.Runtime.CompilerServices.mutableIteratorRemoveErased

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

// subList -> a copying view. ClrSubList implements List (@Clr) so it gets get_Count/get_Item (C3a) + a generated
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
