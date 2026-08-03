/*
 * STAR-PROJECTED COLLECTION MEMBERS — the erased-receiver twin of ClrCollectionDefaults.
 *
 * A collection slot that abandons its element type (`List<*>`, `Collection<*>`, `Set<*>`, `Iterable<*>`) lowers to
 * the NON-generic `System.Collections.IEnumerable`: that is the one CLR type every instantiation is
 * assignment-compatible with, because `IEnumerable<T>` derives from it for every T, value elements included.
 * (`IReadOnlyList<object>` is not: reified generics give a `List<int32>` no reference conversion to it, so the slot
 * is unverifiable and its first interface dispatch faults.)
 *
 * A non-generic view exposes no Kotlin member, so bir2cir routes every member call on such a receiver here. Each
 * helper takes its receiver as `Any` — universally assignable, so the call BOUNDARY needs no conversion — and reads
 * it through the non-generic BCL facades (ClrNestedToString.kt), which every collection implements regardless of
 * element type. This is the collection mirror of ClrMapDefaults, whose map helpers already take `Any` for exactly
 * the same reason; the map side needs no twin here and is routed straight to those.
 *
 * FAST PATH, THEN ENUMERATION. `System.Collections.ICollection`/`IList` give O(1) `Count` and indexed reads, and
 * every BCL collection has them — but a `HashSet<T>` does NOT implement the non-generic `ICollection`, and neither
 * does a pure-Kotlin class implementing `List<E>` (it implements only the generic `IReadOnlyList<E>`). So each
 * helper TESTS for the fast facade and falls back to enumeration, which the universal `IEnumerable` always
 * supports. That is what makes the view sound for every implementer rather than only for the BCL ones.
 */
@file:Suppress("NOTHING_TO_INLINE", "UNCHECKED_CAST")

package kotlin.collections

/** Non-generic `System.Collections.IList` — the indexed/mutating facade of an erased list receiver. `get_Item`
 *  returns `object`, so a value element arrives boxed to `Any?`. */
@kotlin.clr.ClrTypeAlias("System.Collections.IList")
internal interface ClrRawList {
    @kotlin.clr.ClrIntrinsic("get_Item") fun rawGet(index: Int): Any?
    @kotlin.clr.ClrIntrinsic("RemoveAt") fun rawRemoveAt(index: Int): Unit
    @kotlin.clr.ClrIntrinsic("Clear") fun rawClear(): Unit
}

/** The erased element count. `ICollection.Count` when the value has it (every BCL list/dictionary/array does),
 *  else one enumeration pass — a `HashSet<T>` and a pure-Kotlin `List` implementation reach only `IEnumerable`. */
public fun clrStarSize(c: Any): Int {
    if (c is ClrRawCollection) return c.count()
    var n = 0
    val e = (c as ClrRawEnumerable).GetEnumerator()
    while (e.MoveNext()) n++
    return n
}

/** `isEmpty()` — never counts the whole sequence when it does not have to. */
public fun clrStarIsEmpty(c: Any): Boolean {
    if (c is ClrRawCollection) return c.count() == 0
    return !(c as ClrRawEnumerable).GetEnumerator().MoveNext()
}

/** `contains(e)` — Kotlin structural equality over the erased (boxed) elements. */
public fun clrStarContains(c: Any, element: Any?): Boolean {
    val e = (c as ClrRawEnumerable).GetEnumerator()
    while (e.MoveNext()) if (e.current() == element) return true
    return false
}

/** `containsAll(es)` — both sides erased. */
public fun clrStarContainsAll(c: Any, elements: Any): Boolean {
    val e = (elements as ClrRawEnumerable).GetEnumerator()
    while (e.MoveNext()) if (!clrStarContains(c, e.current())) return false
    return true
}

/** `list[index]` — `IList.get_Item` when the value has it, else enumeration to the index. */
public fun clrStarGet(c: Any, index: Int): Any? {
    if (index < 0) throw IndexOutOfBoundsException("Index out of range: " + index)
    if (c is ClrRawList) return c.rawGet(index)
    val e = (c as ClrRawEnumerable).GetEnumerator()
    var i = 0
    while (e.MoveNext()) {
        if (i == index) return e.current()
        i++
    }
    throw IndexOutOfBoundsException("Index out of range: " + index)
}

/** `indexOf(e)` — -1 when absent. */
public fun clrStarIndexOf(c: Any, element: Any?): Int {
    val e = (c as ClrRawEnumerable).GetEnumerator()
    var i = 0
    while (e.MoveNext()) {
        if (e.current() == element) return i
        i++
    }
    return -1
}

/** `lastIndexOf(e)` — -1 when absent. */
public fun clrStarLastIndexOf(c: Any, element: Any?): Int {
    val e = (c as ClrRawEnumerable).GetEnumerator()
    var i = 0
    var found = -1
    while (e.MoveNext()) {
        if (e.current() == element) found = i
        i++
    }
    return found
}

/** `iterator()` — the raw enumerator wrapped as a genuine Kotlin `Iterator<Any?>` (ClrIteratorBridge). */
public fun clrStarIterator(c: Any): Iterator<Any?> = iteratorOverRawEnumerable(c)

/**
 * A BOXED SNAPSHOT of an erased collection as a real `List<Any?>`. The bridge from the non-generic view back to a
 * constructed generic: `subList`/`listIterator` on a star receiver are read-only Kotlin members, so a snapshot is
 * observationally identical for them, and it is the only representation a reified `IReadOnlyList<object>` can take.
 */
public fun clrStarToList(c: Any): List<Any?> {
    val out = ArrayList<Any?>()
    val e = (c as ClrRawEnumerable).GetEnumerator()
    while (e.MoveNext()) out.add(e.current())
    return out
}

/**
 * A BOXED SNAPSHOT of an erased collection or `Array<*>` as an `Array<Any?>`. The array mirror of [clrStarToList]:
 * a `for (x in a)` over a star-projected array keeps its array loop, over the one `object[]` an erased array can
 * legitimately produce. `int32[]` reinterpreted as `object[]` is not a cast but a raw element-storage reinterpret,
 * so copying is the only sound answer.
 */
public fun clrStarToArray(c: Any): Array<Any?> {
    val out = arrayOfNulls<Any?>(clrStarSize(c))
    val e = (c as ClrRawEnumerable).GetEnumerator()
    var i = 0
    while (e.MoveNext()) {
        out[i] = e.current()
        i++
    }
    return out
}

/** `subList(from, to)` over the boxed snapshot. */
public fun clrStarSubList(c: Any, fromIndex: Int, toIndex: Int): List<Any?> =
    clrStarToList(c).subList(fromIndex, toIndex)

/** `listIterator()` / `listIterator(index)` over the boxed snapshot. */
public fun clrStarListIterator(c: Any, index: Int): ListIterator<Any?> =
    clrListListIterator(clrStarToList(c), index)

/** `MutableList.removeAt(i)` — Kotlin returns the removed element; the non-generic `IList.RemoveAt` is void. */
public fun clrStarRemoveAt(c: Any, index: Int): Any? {
    val l = c as ClrRawList
    val old = l.rawGet(index)
    l.rawRemoveAt(index)
    return old
}

/** `MutableCollection.clear()` on an erased receiver. */
public fun clrStarClear(c: Any): Unit = (c as ClrRawList).rawClear()

/** Kotlin-style `[a, b, c]` for an erased collection (the star twin of clrCollToString). */
public fun clrStarToString(c: Any): String = clrElemToString(c)
