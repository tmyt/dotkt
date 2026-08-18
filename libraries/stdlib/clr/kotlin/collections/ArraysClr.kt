/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Returns the array if it's not `null`, or an empty array otherwise.
 * @sample samples.collections.Arrays.Usage.arrayOrEmpty
 */
// A zero-length `T[]` IS an `Array<out T>`, and `dotktNewTypedArray` produces a genuine zero-filled one for a reified
// element without requiring an initializer. `arrayOfNulls<T>(0)` instead has the honest `Array<T?>` representation.
// Written directly (not via emptyArray()) to avoid a nested cross-module inline hop.
public actual inline fun <reified T> Array<out T>?.orEmpty(): Array<out T> =
    this ?: (dotktNewTypedArray(T::class as DotktType, 0) as Array<out T>)

/**
 * Returns a *typed* array containing all the elements of this collection.
 *
 * Allocates an array of runtime type `T` having its size equal to the size of this collection
 * and populates the array with the elements of this collection.
 * @sample samples.collections.Collections.Collections.collectionToTypedArray
 */
// NON-reified, NON-inline: emitted as a method, so its generic-interface calls (size/iterate over the IReadOnly*
// enumerable) get WELL-FORMED refs -- unlike a reified-inline body, where they emit malformed refs (EntryPointNotFound).
@PublishedApi
internal fun <T> dotktCollectionElements(seq: Iterable<T>): Array<Any?> {
    // Take Iterable (=IEnumerable<T>) + count via iteration -- exactly filter's pattern, which works. Avoids the
    // read-only BCL interfaces (IReadOnlyCollection.Count / GetEnumerator-inherited) whose generic refs emit malformed
    // here (EntryPointNotFound); IEnumerable<T>.GetEnumerator direct is fine.
    var count = 0
    for (element in seq) {
        count = count + 1
    }
    val result = arrayOfNulls<Any?>(count)
    var index = 0
    for (element in seq) {
        result[index] = element
        index = index + 1
    }
    return result
}

@Suppress("UNCHECKED_CAST")
public actual inline fun <reified T> Collection<T>.toTypedArray(): Array<T> {
    // The reified function touches ONLY the allocation + the concrete Array<Any?> (Length/index) -- the collection
    // iteration is delegated to the non-reified dotktCollectionElements so no generic-interface ref is emitted in this
    // reified-inline body (which would be a malformed "EntryPointNotFound" ref).
    //
    // `dotktNewTypedArray` allocates the genuine `T[]` this function promises; `arrayOfNulls<T>(n) as Array<T>` cannot,
    // because `arrayOfNulls` returns `Array<T?>` = `object[]` (#86 D2) and `object[]` is not castable to `int32[]`.
    // Each element narrows out of the boxed staging array as it is written.
    val objs = dotktCollectionElements<T>(this)
    val result = dotktNewTypedArray(T::class as DotktType, objs.size) as Array<T>
    var i = 0
    while (i < objs.size) {
        result[i] = objs[i] as T
        i = i + 1
    }
    return result
}

/** Internal unsafe construction of array based on reference array type */
// The declared `Array<T>` is a `!T[]` whose slots this helper's callers overwrite, so it needs a ZERO-FILLED genuine
// `T[]` — which no Kotlin expression names: the array constructor demands an element the caller has none of, and
// `arrayOfNulls<T>(size)` honestly returns `Array<T?>` = `object[]` (#86 D2), unrelated to `int32[]`. So it is built
// the way the JVM builds it, from `reference`'s RUNTIME array type — which on the CLR is exactly the reified `T[]`.
// `CreateInstanceFromArrayType` zero-fills, so the result already reads back as `default(T)`/null in every slot.
@kotlin.clr.ClrIntrinsic("GetType")                                     // object.GetType() -> the receiver's runtime array Type
private fun Any.arrClrGetType(): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstanceFromArrayType")    // (Type arrayType, int length) -> Array; receiver(arrayType) -> arg0
private fun Any.arrClrCreateArrayLike(length: Int): Any = TODO("clr binding should be implemented")

internal actual fun <T> arrayOfNulls(reference: Array<T>, size: Int): Array<T> =
    (reference as Any).arrClrGetType().arrClrCreateArrayLike(size) as Array<T>
