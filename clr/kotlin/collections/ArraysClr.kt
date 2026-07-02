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
// `arrayOfNulls<T>(0)` lowers to `newarr !T` (kotc newArraySized) — a zero-length T[] IS an Array<out T>.
// Written directly (not via emptyArray()) to avoid a nested cross-module inline hop.
public actual inline fun <reified T> Array<out T>?.orEmpty(): Array<out T> = this ?: (arrayOfNulls<T>(0) as Array<out T>)

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
    // The reified function touches ONLY arrayOfNulls<T> (newarr) + the concrete Array<Any?> (Length/index) -- the
    // collection iteration is delegated to the non-reified dotktCollectionElements so no generic-interface ref is
    // emitted in this reified-inline body (which would be a malformed "EntryPointNotFound" ref).
    val objs = dotktCollectionElements<T>(this)
    val result = arrayOfNulls<T>(objs.size)
    var i = 0
    while (i < objs.size) {
        result[i] = objs[i] as T
        i = i + 1
    }
    return result as Array<T>
}

/** Internal unsafe construction of array based on reference array type */
// The JVM re-allocates via `reference`'s RUNTIME component type (java.lang.reflect.Array.newInstance). On the CLR
// generics are reified, so the STATIC instantiation `newarr !T` already carries the exact element type — `reference`
// is unused. TYPE_PARAMETER_AS_REIFIED is suppressed deliberately: kotc lowers `arrayOfNulls<T>(size)` for a
// non-reified T to the same generic `newarr !T` (see MEMORY clr-all-type-args-reified); the JVM erasure hazard the
// diagnostic guards against does not exist here.
@Suppress("TYPE_PARAMETER_AS_REIFIED", "UNUSED_PARAMETER")
internal actual fun <T> arrayOfNulls(reference: Array<T>, size: Int): Array<T> = arrayOfNulls<T>(size) as Array<T>
