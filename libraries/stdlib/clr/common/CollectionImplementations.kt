/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

package kotlin.collections

// The upstream bodies use JVM array erasure: Comparable<T>[]/Any?[] is unchecked-cast to T[] before sorting.
// CLR arrays are reified, so that cast is neither a view nor a valid representation conversion (notably for value
// types). Keep the Kotlin-visible declarations upstream-identical and bind these CLR-owned bodies to them through the
// stdlib declaration-identity overlay.
internal fun <T : Comparable<T>> Iterable<T>.clrSortedImplementation(): List<T> =
    toMutableList().apply { sort() }

internal fun <T> Iterable<T>.clrSortedWithImplementation(comparator: Comparator<in T>): List<T> =
    toMutableList().apply { sortWith(comparator) }
