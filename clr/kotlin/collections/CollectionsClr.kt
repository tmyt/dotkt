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
public actual fun <T> listOf(element: T): List<T> = TODO("clr binding should be implemented")

/**
 * Returns a new [ArrayList] from the given Array.
 */
@kotlin.internal.InlineOnly
internal actual inline fun <T> Array<out T>.asArrayList(): ArrayList<T> = TODO("clr binding should be implemented")

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildListInternal(builderAction: MutableList<E>.() -> Unit): List<E> = TODO("clr binding should be implemented")

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildListInternal(capacity: Int, builderAction: MutableList<E>.() -> Unit): List<E> = TODO("clr binding should be implemented")

/**
 * Returns a new list with the elements of this collection randomly shuffled.
 */
@SinceKotlin("1.2")
public actual fun <T> Iterable<T>.shuffled(): List<T> = TODO("clr binding should be implemented")

@Suppress("DEPRECATION_ERROR")
@kotlin.internal.InlineOnly
internal actual inline fun collectionToArray(collection: Collection<*>): Array<Any?> = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
@Suppress("UNCHECKED_CAST", "DEPRECATION_ERROR")
internal actual inline fun <T> collectionToArray(collection: Collection<*>, array: Array<T>): Array<T> = TODO("clr binding should be implemented")

internal actual fun <T> terminateCollectionToArray(collectionSize: Int, array: Array<T>): Array<T> = TODO("clr binding should be implemented")

// copies typed varargs array to array of objects
internal actual fun <T> Array<out T>.copyToArrayOfAny(isVarargs: Boolean): Array<out Any?> = TODO("clr binding should be implemented")

@PublishedApi
@SinceKotlin("1.3")
@InlineOnly
internal actual inline fun checkIndexOverflow(index: Int): Int = TODO("clr binding should be implemented")

@PublishedApi
@SinceKotlin("1.3")
@InlineOnly
internal actual inline fun checkCountOverflow(count: Int): Int = TODO("clr binding should be implemented")
