/*
 * Copyright 2010-2023 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

import kotlin.internal.InlineOnly

/**
 * Returns a new read-only list containing only the specified object [element].
 *
 * The returned list is serializable.
 *
 * @sample samples.collections.Collections.Lists.singletonReadOnlyList
 */
public actual fun <T> listOf(element: T): List<T> {
    val list = ArrayList<T>(1)
    list.add(element)
    return list
}

/**
 * Returns a new [ArrayList] from the given Array.
 */
@kotlin.internal.InlineOnly
internal actual inline fun <T> Array<out T>.asArrayList(): ArrayList<T> {
    val list = ArrayList<T>(this.size)
    for (element in this) list.add(element)
    return list
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildListInternal(builderAction: MutableList<E>.() -> Unit): List<E> {
    val list = ArrayList<E>()
    builderAction(list)
    return list
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildListInternal(capacity: Int, builderAction: MutableList<E>.() -> Unit): List<E> {
    val list = ArrayList<E>(capacity)
    builderAction(list)
    return list
}

/**
 * Returns a new list with the elements of this collection randomly shuffled.
 */
@SinceKotlin("1.2")
public actual fun <T> Iterable<T>.shuffled(): List<T> {
    val list = toMutableList()
    list.shuffle()
    return list
}

@Suppress("DEPRECATION_ERROR")
@kotlin.internal.InlineOnly
internal actual inline fun collectionToArray(collection: Collection<*>): Array<Any?> {
    val result = arrayOfNulls<Any?>(collection.size)
    var index = 0
    for (element in collection) {
        result[index++] = element
    }
    return result
}

// Fills the supplied [array] with the collection's elements and null-terminates it (`terminateCollectionToArray`
// stores a null right after the last element when the array is larger). Assumes `array` is at least `collection.size`
// long — the JVM's reflective re-allocation when it is too small relies on the generic ref-array alloc that is blocked
// on CLR for non-reified T (see ArraysClr.arrayOfNulls(reference, size)).
@kotlin.internal.InlineOnly
@Suppress("UNCHECKED_CAST", "DEPRECATION_ERROR")
internal actual inline fun <T> collectionToArray(collection: Collection<*>, array: Array<T>): Array<T> {
    var index = 0
    for (element in collection) {
        (array as Array<Any?>)[index] = element
        index++
    }
    return terminateCollectionToArray(collection.size, array)
}

internal actual fun <T> terminateCollectionToArray(collectionSize: Int, array: Array<T>): Array<T> {
    if (collectionSize < array.size) {
        @Suppress("UNCHECKED_CAST")
        (array as Array<T?>)[collectionSize] = null
    }
    return array
}

// copies typed varargs array to array of objects
internal actual fun <T> Array<out T>.copyToArrayOfAny(isVarargs: Boolean): Array<out Any?> {
    val result = arrayOfNulls<Any?>(this.size)
    for (index in this.indices) {
        result[index] = this[index]
    }
    return result
}

@PublishedApi
@SinceKotlin("1.3")
@InlineOnly
internal actual inline fun checkIndexOverflow(index: Int): Int {
    if (index < 0) {
        throwIndexOverflow()
    }
    return index
}

@PublishedApi
@SinceKotlin("1.3")
@InlineOnly
internal actual inline fun checkCountOverflow(count: Int): Int {
    if (count < 0) {
        throwCountOverflow()
    }
    return count
}
