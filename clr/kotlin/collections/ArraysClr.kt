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
public actual inline fun <reified T> Array<out T>?.orEmpty(): Array<out T> = TODO("clr binding should be implemented")

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
internal actual fun <T> arrayOfNulls(reference: Array<T>, size: Int): Array<T> = TODO("clr binding should be implemented")
