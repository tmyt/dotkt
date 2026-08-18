/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// The collection/array FACTORY binding markers. Defined in the COMMON source set (not the platform
// clr/kotlin/clr/ClrIntrinsic.kt) because they annotate factory bodies that live in COMMON stdlib sources — `listOf`/
// `setOf`/`mapOf` in kotlin.collections, the unsigned `ubyteArrayOf`/... array factories — and a common source cannot
// reference a platform-only declaration under the jar's multi-platform (`-Xcommon-sources`) compile. Placed in common,
// both common and platform (clr/builtins/Library.kt) sources can annotate their factories. The other kotlin.clr bindings
// (@ClrIntrinsic/@ClrTypeAlias/@ClrConv/@ClrProperty/@ClrRefArgument) stay platform-only.
package kotlin.clr

// Marks a collection FACTORY top-level function (`listOf`/`setOf`/`mapOf`/`mutableListOf`/`emptyList`/...): a call to it
// CONSTRUCTS the backing BCL collection directly (`kind` = "list" -> `List<T>`, "set" -> `HashSet<T>`, "map" ->
// `Dictionary<K,V>`) instead of calling the Kotlin body. bir2cir reads this marker off the REFERENCE assembly (NOT kotc)
// and emits the `{k:newList/newSet/newMap}` node — the SAME node kotc used to synthesize from its retired
// LIST_FACTORIES/SET_FACTORIES/MAP_FACTORIES name tables. The element/key/value TYPES come from the call's `typeArgs`
// (`typeArgs[0]` for the list/set element, `[0]`/`[1]` for the map key/value); the ELEMENTS from the single vararg
// argument (kotc emits it as a `newArray` node), the lone non-vararg element, or none (`emptyList()`). `mapOf`
// additionally splits each `a to b` Pair-LITERAL argument (a `new kotlin.Pair(k,v)` node) into a key/value entry — but a
// NON-literal Pair argument (`mapOf(pairVariable)`) is left as a plain call to the real `mapOf` body (never force-split).
@Target(AnnotationTarget.FUNCTION)
public annotation class ClrCollectionFactory(val kind: String)

// Marks an array FACTORY top-level function (`arrayOf`/`intArrayOf`/.../`arrayOfNulls`): a call to it CONSTRUCTS a native
// CLR array. bir2cir reads this marker off the REFERENCE assembly and emits `{k:newArray}` (from the vararg elements) for
// `kind` = "vararg", or `{k:newArraySized}` (from the `size` argument) for `kind` = "sized" (`arrayOfNulls`). The element
// type comes from `typeArgs[0]` (the generic `arrayOf<T>`/`arrayOfNulls<T>`) or the vararg element type (the concrete
// `intArrayOf`/... primitive factories). Replaces kotc's retired ARRAY_FACTORY_NAMES recognition.
@Target(AnnotationTarget.FUNCTION)
public annotation class ClrArrayFactory(val kind: String)

// Marks Sequence.filterNotNull's CLR representation. A `Sequence<T?>` has an object element when T may be a value
// type, so its lazy wrapper cannot be unchecked-cast to the reified `Sequence<T>` / `IEnumerable<T>` result. bir2cir
// consumes this trusted stdlib declaration fact and constructs the CLR-specific element-converting adapter.
// Internal so this compiler binding cannot be named by user source.
@Target(AnnotationTarget.FUNCTION)
@Retention(AnnotationRetention.BINARY)
internal annotation class ClrSequenceFilterNotNull

// Marks an inline stdlib declaration whose body has already filtered a Sequence<Any?> to its reified result type,
// but whose final unchecked Sequence<R> cast cannot change the CLR IEnumerable element interface. bir2cir replaces
// that declaration-local cast with the typed sequence view; the original predicate remains ordinary Kotlin code.
@Target(AnnotationTarget.FUNCTION)
@Retention(AnnotationRetention.BINARY)
internal annotation class ClrSequenceElementAdapter
