/*
 * Copyright 2010-2023 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// CLR actuals for the map factories/helpers. All BODY (pure Kotlin factories over the @ClrIntrinsic LinkedHashMap).

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Returns a new read-only map, mapping only the specified key to the
 * specified value.
 *
 * The returned map is serializable.
 *
 * @sample samples.collections.Maps.Instantiation.mapFromPairs
 */
public actual fun <K, V> mapOf(pair: Pair<K, V>): Map<K, V> {
    val map = LinkedHashMap<K, V>(mapCapacity(1))
    map.put(pair.first, pair.second)
    return map
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <K, V> buildMapInternal(builderAction: MutableMap<K, V>.() -> Unit): Map<K, V> {
    val map = LinkedHashMap<K, V>()
    builderAction(map)
    return map
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <K, V> buildMapInternal(capacity: Int, builderAction: MutableMap<K, V>.() -> Unit): Map<K, V> {
    val map = LinkedHashMap<K, V>(mapCapacity(capacity))
    builderAction(map)
    return map
}

// creates a singleton copy of map, if there is specialization available in target platform, otherwise returns itself
@kotlin.internal.InlineOnly
internal actual inline fun <K, V> Map<K, V>.toSingletonMapOrSelf(): Map<K, V> = this

// creates a singleton copy of map
internal actual fun <K, V> Map<out K, V>.toSingletonMap(): Map<K, V> {
    val result = LinkedHashMap<K, V>()
    result.putAll(this)
    return result
}

/**
 * Calculate the initial capacity of a map, based on Guava's
 * [com.google.common.collect.Maps.capacity](https://github.com/google/guava/blob/v28.2/guava/src/com/google/common/collect/Maps.java#L325)
 * approach.
 */
@PublishedApi
internal actual fun mapCapacity(expectedSize: Int): Int = when {
    // We are not coercing the value to a valid one and not throwing an exception. It is up to the caller to
    // properly handle negative values.
    expectedSize < 0 -> expectedSize
    expectedSize < 3 -> expectedSize + 1
    expectedSize < (1 shl 30) -> ((expectedSize / 0.75F) + 1.0F).toInt()
    // any large value
    else -> Int.MAX_VALUE
}
