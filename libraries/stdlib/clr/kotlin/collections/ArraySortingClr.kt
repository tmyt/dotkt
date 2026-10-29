/*
 * Copyright 2010-2018 JetBrains s.r.o. Use of this source code is governed by the Apache 2.0 license
 * that can be found in the LICENSE file.
 */

package kotlin.collections

// Difference-based midpoints avoid signed overflow for valid large CLR array indices.


private fun <T: Comparable<T>> mergeSort(array: Array<T>, start: Int, endInclusive: Int) {
    @Suppress("UNCHECKED_CAST")
    val buffer = arrayOfNulls<Any?>(array.size) as Array<T>
    val result = mergeSort(array, buffer, start, endInclusive)
    if (result !== array) {
        for (i in start..endInclusive) array[i] = result[i]
    }
}

// Both start and end are inclusive indices.
private fun <T: Comparable<T>> mergeSort(array: Array<T>, buffer: Array<T>, start: Int, end: Int): Array<T> {
    if (start == end) {
        return array
    }

    val median = start + (end - start) / 2
    val left = mergeSort(array, buffer, start, median)
    val right = mergeSort(array, buffer, median + 1, end)

    val target = if (left === buffer) array else buffer

    // Merge.
    var leftIndex = start
    var rightIndex = median + 1
    for (i in start..end) {
        when {
            leftIndex <= median && rightIndex <= end -> {
                val leftValue = left[leftIndex]
                val rightValue = right[rightIndex]

                if (leftValue <= rightValue) {
                    target[i] = leftValue
                    leftIndex++
                } else {
                    target[i] = rightValue
                    rightIndex++
                }
            }
            leftIndex <= median -> {
                target[i] = left[leftIndex]
                leftIndex++
            }
            else /* rightIndex <= end */ -> {
                target[i] = right[rightIndex]
                rightIndex++
            }
        }
    }

    return target
}

// Sort with comparator.
private fun <T> mergeSort(array: Array<T>, start: Int, endInclusive: Int, comparator: Comparator<T>) {
    @Suppress("UNCHECKED_CAST")
    val buffer = arrayOfNulls<Any?>(array.size) as Array<T>
    val result = mergeSort(array, buffer, start, endInclusive, comparator)
    if (result !== array) {
        for (i in start..endInclusive) array[i] = result[i]
    }
}

// Both start and end are inclusive indices.
private fun <T> mergeSort(array: Array<T>, buffer: Array<T>, start: Int, end: Int, comparator: Comparator<T>): Array<T> {
    if (start == end) {
        return array
    }

    val median = start + (end - start) / 2
    val left = mergeSort(array, buffer, start, median, comparator)
    val right = mergeSort(array, buffer, median + 1, end, comparator)

    val target = if (left === buffer) array else buffer

    // Merge.
    var leftIndex = start
    var rightIndex = median + 1
    for (i in start..end) {
        when {
            leftIndex <= median && rightIndex <= end -> {
                val leftValue = left[leftIndex]
                val rightValue = right[rightIndex]

                if (comparator.compare(leftValue, rightValue) <= 0) {
                    target[i] = leftValue
                    leftIndex++
                } else {
                    target[i] = rightValue
                    rightIndex++
                }
            }
            leftIndex <= median -> {
                target[i] = left[leftIndex]
                leftIndex++
            }
            else /* rightIndex <= end */ -> {
                target[i] = right[rightIndex]
                rightIndex++
            }
        }
    }

    return target
}

/**
 * Sorts the subarray specified by [fromIndex] (inclusive) and [toIndex] (exclusive) parameters
 * using the merge sort algorithm with the given [comparator].
 */
internal fun <T> sortArrayWith(array: Array<out T>, fromIndex: Int, toIndex: Int, comparator: Comparator<T>) {
    if (fromIndex < toIndex - 1) {
        @Suppress("UNCHECKED_CAST")
        mergeSort(array as Array<T>, fromIndex, toIndex - 1, comparator)
    }
}

/**
 * Sorts a subarray of [Comparable] elements specified by [fromIndex] (inclusive) and
 * [toIndex] (exclusive) parameters using the merge sort algorithm.
 */
internal fun <T: Comparable<T>> sortArray(array: Array<out T>, fromIndex: Int, toIndex: Int) {
    if (fromIndex < toIndex - 1) {
        @Suppress("UNCHECKED_CAST")
        mergeSort(array as Array<T>, fromIndex, toIndex - 1)
    }
}
