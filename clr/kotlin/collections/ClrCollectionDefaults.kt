/*
 * CLR ref/runtime split: default implementations for Collection/List members that have NO equivalent on the substituted
 * BCL types (IReadOnlyCollection<T> / IReadOnlyList<T> only expose Count / this[i] / GetEnumerator). The backend routes a
 * member call `recv.isEmpty()` / `recv.contains(e)` / ... to these statics (mirrors the iterator() bridge). The bodies use
 * ONLY BCL-bound members (size->Count, get->get_Item, iterator->GetEnumerator) so they never recurse into a routed member.
 * See docs/design-clr-stdlib-ref-runtime-split.md "non-BCL members".
 */
@file:Suppress("NOTHING_TO_INLINE")

package kotlin.collections

public fun <T> clrCollIsEmpty(c: Collection<T>): Boolean = c.size == 0

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
        sb.append(x.toString())
    }
    sb.append("]")
    return sb.toString()
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
