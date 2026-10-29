/*
 * Copyright 2010-2020 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Sorts elements in the list in-place according to their natural sort order.
 *
 * The sort is _stable_. It means that equal elements preserve their order relative to each other after sorting.
 *
 * @sample samples.collections.Collections.Sorting.sortMutableList
 */
public actual fun <T : Comparable<T>> MutableList<T>.sort(): Unit = sortWith(naturalOrder())

/**
 * Sorts elements in the list in-place according to the order specified with [comparator].
 *
 * The sort is _stable_. It means that equal elements preserve their order relative to each other after sorting.
 *
 * @sample samples.collections.Collections.Sorting.sortMutableListWith
 */
public actual fun <T> MutableList<T>.sortWith(comparator: Comparator<in T>): Unit {
    if (size > 1) {
        val it = listIterator()
        val sortedArray = @Suppress("UNCHECKED_CAST") (toTypedArray<Any?>() as Array<T>).apply { sortWith(comparator) }
        for (v in sortedArray) {
            val _ = it.next()
            it.set(v)
        }
    }
}

/**
 * Fills the list with the provided [value].
 *
 * Each element in the list gets replaced with the [value].
 */
@kotlin.internal.InlineOnly
@SinceKotlin("1.2")
public actual inline fun <T> MutableList<T>.fill(value: T) {
    for (index in 0 until size) this[index] = value
}

/**
 * Randomly shuffles elements in this mutable list.
 */
@kotlin.internal.InlineOnly
@SinceKotlin("1.2")
public actual inline fun <T> MutableList<T>.shuffle() {
    // Fisher-Yates over size/get/set using the default platform Random.
    for (i in size - 1 downTo 1) {
        val j = kotlin.random.Random.Default.nextInt(i + 1)
        val tmp = this[i]
        this[i] = this[j]
        this[j] = tmp
    }
}
