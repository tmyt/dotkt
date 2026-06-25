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
