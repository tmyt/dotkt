/*
 * The DotKt standard library — REAL Kotlin source, compiled by DotKt's own toolchain into DotKt.Stdlib.dll, replacing
 * the compiler's hand-written `COLLECTION_OPS` LINQ lowerings one op at a time. Ops here are removed from the
 * `COLLECTION_OPS` catalog (BirMappings.kt); a call to one routes (round-trip registry) to the real body below instead.
 * `kotlin.collections.List<T>` maps to the BCL `System.Collections.Generic.List<T>` (this[i] -> get_Item, size -> Count),
 * so random-access ops run directly on it with no iteration. Sourced from the vendored kotlin-stdlib (_Collections.kt).
 *
 * NOTE: ops are deliberately NOT `inline` (the upstream stdlib marks getOrElse inline). A migrated op is consumed as a
 * cross-assembly call into DotKt.Stdlib; making it inline would make the injector also emit a local body stub per
 * consumer (unused, since an inline EXTENSION is called not spliced), which fails ilverify. Non-inline = one clean call.
 */
package kotlin.collections

/** Returns an element at [index] or the result of [defaultValue] for an out-of-bounds [index]. */
public fun <T> List<T>.getOrElse(index: Int, defaultValue: (Int) -> T): T =
    if (index >= 0 && index <= size - 1) this[index] else defaultValue(index)

/** Returns a list containing the results of applying [transform] to each element. (Real stdlib body; runs on the BCL
 *  list — iterate, build an ArrayList. NON-inline so the consumer calls it rather than splicing/re-emitting a stub.) */
public fun <T, R> Iterable<T>.map(transform: (T) -> R): List<R> {
    val destination = ArrayList<R>()
    for (item in this) destination.add(transform(item))
    return destination
}

/** Returns a list containing only the elements matching [predicate]. */
public fun <T> Iterable<T>.filter(predicate: (T) -> Boolean): List<T> {
    val destination = ArrayList<T>()
    for (item in this) if (predicate(item)) destination.add(item)
    return destination
}

/** Performs [action] on each element. */
public fun <T> Iterable<T>.forEach(action: (T) -> Unit) {
    for (element in this) action(element)
}

/** Returns the number of elements. */
public fun <T> Iterable<T>.count(): Int {
    var count = 0
    for (element in this) count++
    return count
}

/** Returns the number of elements matching [predicate]. */
public fun <T> Iterable<T>.count(predicate: (T) -> Boolean): Int {
    var count = 0
    for (element in this) if (predicate(element)) count++
    return count
}

/** Accumulates value starting with [initial] and applying [operation] left to right. */
public fun <T, R> Iterable<T>.fold(initial: R, operation: (acc: R, T) -> R): R {
    var accumulator = initial
    for (element in this) accumulator = operation(accumulator, element)
    return accumulator
}
