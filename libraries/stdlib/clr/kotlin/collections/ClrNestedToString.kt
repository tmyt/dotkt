/*
 * Nested collection/map stringification (final-review N7). The generic `clrCollToString<T>` / `clrMapToString<K,V>`
 * render the TOP-LEVEL collection — there `T`/`K`/`V` are statically known, so `for (x in c)` binds the generic BCL
 * enumerator and each element's static type is exact. A NESTED element, however, arrives as `Any?` (erased): calling
 * `.toString()` on a nested `List<Int>` hits .NET's `Object.ToString()` and prints the raw `System.Collections.Generic.
 * List`1[System.Int32]` instead of Kotlin's `[1, 2]`.
 *
 * Fix: route every rendered element through [clrElemToString], which detects a nested collection/map at RUNTIME and
 * recurses. Two constraints force the non-generic BCL facades below rather than a re-dispatch to the generic helpers:
 *   1. Re-dispatching an erased element to `clrCollToString<Any?>` would infer `Collection<object>` = `IReadOnlyCollection
 *      <object>`, which a runtime `List<Int>` is NOT assignable to (no value-type covariance on the CLR) -> InvalidCast.
 *   2. `StarProjectionLowering` (which rewrites `is Collection<*>` / star-member-calls to the non-generic BCL interface)
 *      runs ONLY in app builds, NOT this stdlib self-build — so `is Collection<*>` here would isinst the *generic*
 *      `IReadOnlyCollection<object>` and MISS a `List<Int>`.
 * Every substituted BCL collection (`List<T>`/`HashSet<T>`) implements the non-generic `System.Collections.ICollection`
 * and every dictionary the non-generic `System.Collections.IDictionary`, REGARDLESS of element type — so detection and
 * iteration both go through these erased facades, whose enumerators surface each element already boxed to `Any?`.
 */
@file:Suppress("NOTHING_TO_INLINE", "ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT")

package kotlin.collections

/** Non-generic `System.Collections.ICollection` — the detection facade for "is this an erased collection?".
 *  `Count` is the non-generic size accessor (IDictionary : ICollection), read covariance-safely (value-type-independent). */
@kotlin.clr.ClrTypeAlias("System.Collections.ICollection")
internal interface ClrRawCollection {
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Count") fun count(): Int
}

/** Non-generic `System.Collections.IEnumerable` — its `GetEnumerator()` yields the erased (`Current: Any?`) enumerator. */
@kotlin.clr.ClrTypeAlias("System.Collections.IEnumerable")
internal interface ClrRawEnumerable {
    fun GetEnumerator(): ClrRawEnumerator
}

/** Non-generic `System.Collections.IEnumerator` — `Current` is `object`, so a value-type element arrives boxed to Any?. */
@kotlin.clr.ClrTypeAlias("System.Collections.IEnumerator")
internal interface ClrRawEnumerator {
    fun MoveNext(): Boolean
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Current") fun current(): Any?
}

/** Non-generic `System.Collections.IDictionary` — detection facade + its `IDictionaryEnumerator` (Key/Value: Any?).
 *  `Contains(object)` / `get_Item(object): object` are the covariance-safe key-test / value-read (every `Dictionary<K,V>`
 *  implements this base interface regardless of V, so they never hit the generic value-type invariance of IDictionary<K,V>). */
@kotlin.clr.ClrTypeAlias("System.Collections.IDictionary")
internal interface ClrRawDictionary {
    fun GetEnumerator(): ClrRawDictionaryEnumerator
    fun Contains(key: Any?): Boolean
    @kotlin.clr.ClrIntrinsic("get_Item") fun rawGet(key: Any?): Any?
    @kotlin.clr.ClrIntrinsic("set_Item") fun rawSet(key: Any?, value: Any?): Unit
    /** Non-generic `IDictionary.Remove(object)` — VOID (unlike generic `IDictionary<K,V>.Remove(K): bool`). */
    @kotlin.clr.ClrIntrinsic("Remove") fun rawRemove(key: Any?): Unit
    /** Non-generic `IDictionary.Clear()` — the erased `MutableMap.clear()` slot (ClrStarProjection.clrStarClear). */
    @kotlin.clr.ClrIntrinsic("Clear") fun rawClear(): Unit
}

/** Non-generic `System.Collections.IDictionaryEnumerator` — `Key`/`Value` are `object` (erased entry access). */
@kotlin.clr.ClrTypeAlias("System.Collections.IDictionaryEnumerator")
internal interface ClrRawDictionaryEnumerator {
    fun MoveNext(): Boolean
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Key") fun key(): Any?
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Value") fun value(): Any?
}

/**
 * Kotlin-style rendering of one element that may itself be a nested collection/map. A dictionary is ALSO an ICollection
 * (IDictionary : ICollection), so the map test MUST precede the collection test. A `String` is neither ICollection nor
 * IDictionary, so it falls to plain `toString()`.
 */
public fun clrElemToString(x: Any?): String = when {
    x == null -> "null"
    x is ClrRawDictionary -> clrRawMapToString(x)
    x is ClrRawCollection -> clrRawCollToString(x)
    else -> x.toString()
}

/** `[a, b, c]` over a non-generic enumerable, recursing per element. */
private fun clrRawCollToString(c: ClrRawCollection): String {
    val e = (c as ClrRawEnumerable).GetEnumerator()
    val sb = StringBuilder()
    sb.append("[")
    var first = true
    while (e.MoveNext()) {
        if (!first) sb.append(", ")
        first = false
        sb.append(clrElemToString(e.current()))
    }
    sb.append("]")
    return sb.toString()
}

/** `{k=v, ...}` over a non-generic dictionary enumerator, recursing per key and value. */
private fun clrRawMapToString(m: ClrRawDictionary): String {
    val e = m.GetEnumerator()
    val sb = StringBuilder()
    sb.append("{")
    var first = true
    while (e.MoveNext()) {
        if (!first) sb.append(", ")
        first = false
        sb.append(clrElemToString(e.key()))
        sb.append("=")
        sb.append(clrElemToString(e.value()))
    }
    sb.append("}")
    return sb.toString()
}
