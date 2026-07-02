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
public actual fun <T : Comparable<T>> MutableList<T>.sort(): Unit = sortWith(Comparator { a, b -> a.compareTo(b) })

/**
 * Sorts elements in the list in-place according to the order specified with [comparator].
 *
 * The sort is _stable_. It means that equal elements preserve their order relative to each other after sorting.
 *
 * @sample samples.collections.Collections.Sorting.sortMutableListWith
 */
public actual fun <T> MutableList<T>.sortWith(comparator: Comparator<in T>): Unit {
    // STABLE O(n log n) bottom-up merge sort. Copy out to an Any?[] aux (`Array<Any?>(n){null}` -> object[]; cast
    // per-element for compare), sort, write back via set. kotlin.Comparator is a plain fun interface (a SAM
    // `Comparator { a, b -> ... }` lowers to a synthetic impl, not a Func delegate). Value-type instantiations work via
    // the ilemit `castclass`->`unbox.any` fix + the get_Item(ret=gp:T)/arraySet(box) value<->collection boundary fixes.
    // (The earlier "IComparer alias / PersistedAssemblyBuilder limitation" was a misdiagnosis -- it was 3 small mistypings.)
    val n = this.size
    if (n < 2) return
    val a = Array<Any?>(n) { null }
    for (i in 0 until n) a[i] = this.get(i)
    val tmp = Array<Any?>(n) { null }
    var width = 1
    while (width < n) {
        var lo = 0
        while (lo < n) {
            val mid = if (lo + width < n) lo + width else n
            val hi = if (lo + 2 * width < n) lo + 2 * width else n
            var l = lo; var r = mid; var k = lo
            while (l < mid && r < hi) {
                if (comparator.compare(a[l] as T, a[r] as T) <= 0) { tmp[k] = a[l]; l = l + 1 }
                else { tmp[k] = a[r]; r = r + 1 }
                k = k + 1
            }
            while (l < mid) { tmp[k] = a[l]; l = l + 1; k = k + 1 }
            while (r < hi) { tmp[k] = a[r]; r = r + 1; k = k + 1 }
            var j = lo
            while (j < hi) { a[j] = tmp[j]; j = j + 1 }
            lo = lo + 2 * width
        }
        width = width * 2
    }
    for (i in 0 until n) this.set(i, a[i] as T)
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
