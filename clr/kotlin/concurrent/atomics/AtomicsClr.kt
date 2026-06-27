// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
@file:OptIn(ExperimentalAtomicApi::class)

package kotlin.concurrent.atomics

import kotlin.internal.InlineOnly

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.update(transform: (Int) -> Int): Unit { TODO("clr binding should be implemented") }

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.fetchAndUpdate(transform: (Int) -> Int): Int = TODO("clr binding should be implemented")

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.updateAndFetch(transform: (Int) -> Int): Int = TODO("clr binding should be implemented")

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.update(transform: (Long) -> Long): Unit { TODO("clr binding should be implemented") }

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.fetchAndUpdate(transform: (Long) -> Long): Long = TODO("clr binding should be implemented")

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.updateAndFetch(transform: (Long) -> Long): Long = TODO("clr binding should be implemented")

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.update(transform: (T) -> T): Unit { TODO("clr binding should be implemented") }

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.fetchAndUpdate(transform: (T) -> T): T = TODO("clr binding should be implemented")

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.updateAndFetch(transform: (T) -> T): T = TODO("clr binding should be implemented")
