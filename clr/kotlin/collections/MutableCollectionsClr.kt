/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Sorts elements in the list in-place according to their natural sort order.
 *
 * The sort is _stable_. It means that equal elements preserve their order relative to each other after sorting.
 *
 * @sample samples.collections.Collections.Sorting.sortMutableList
 */
public actual fun <T : Comparable<T>> MutableList<T>.sort(): Unit { TODO("clr binding should be implemented") }

/**
 * Sorts elements in the list in-place according to the order specified with [comparator].
 *
 * The sort is _stable_. It means that equal elements preserve their order relative to each other after sorting.
 *
 * @sample samples.collections.Collections.Sorting.sortMutableListWith
 */
public actual fun <T> MutableList<T>.sortWith(comparator: Comparator<in T>): Unit { TODO("clr binding should be implemented") }

/**
 * Fills the list with the provided [value].
 *
 * Each element in the list gets replaced with the [value].
 */
@kotlin.internal.InlineOnly
@SinceKotlin("1.2")
public actual inline fun <T> MutableList<T>.fill(value: T) { TODO("clr binding should be implemented") }

/**
 * Randomly shuffles elements in this mutable list.
 */
@kotlin.internal.InlineOnly
@SinceKotlin("1.2")
public actual inline fun <T> MutableList<T>.shuffle() { TODO("clr binding should be implemented") }
