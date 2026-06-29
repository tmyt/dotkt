// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// The genuinely-atomic ops are CAS loops over the class's own compareAndSet (see builtins/Atomics.kt).
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
@file:OptIn(ExperimentalAtomicApi::class)

package kotlin.concurrent.atomics

import kotlin.internal.InlineOnly

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.update(transform: (Int) -> Int): Unit {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.fetchAndUpdate(transform: (Int) -> Int): Int {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return old }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicInt.updateAndFetch(transform: (Int) -> Int): Int {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return new }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.update(transform: (Long) -> Long): Unit {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.fetchAndUpdate(transform: (Long) -> Long): Long {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return old }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLong.updateAndFetch(transform: (Long) -> Long): Long {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return new }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.update(transform: (T) -> T): Unit {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.fetchAndUpdate(transform: (T) -> T): T {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return old }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicReference<T>.updateAndFetch(transform: (T) -> T): T {
    while (true) { val old = load(); val new = transform(old); if (compareAndSet(old, new)) return new }
}
