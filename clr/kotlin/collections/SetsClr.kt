/*
 * Copyright 2010-2023 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// CLR actuals for the set factories/builders. All BODY (pure Kotlin factories over the @ClrIntrinsic LinkedHashSet).

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Returns a new read-only set containing only the specified object [element].
 *
 * The returned set is serializable.
 *
 * @sample samples.collections.Collections.Sets.singletonReadOnlySet
 */
public actual fun <T> setOf(element: T): Set<T> {
    val set = LinkedHashSet<T>(mapCapacity(1))
    set.add(element)
    return set
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildSetInternal(builderAction: MutableSet<E>.() -> Unit): Set<E> {
    val set = LinkedHashSet<E>()
    builderAction(set)
    return set
}

@PublishedApi
@SinceKotlin("1.3")
@kotlin.internal.InlineOnly
internal actual inline fun <E> buildSetInternal(capacity: Int, builderAction: MutableSet<E>.() -> Unit): Set<E> {
    val set = LinkedHashSet<E>(mapCapacity(capacity))
    builderAction(set)
    return set
}
