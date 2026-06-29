// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// The genuinely-atomic ops are CAS loops over the class's own compareAndSetAt (see builtins/AtomicArrays.kt).
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
@file:OptIn(ExperimentalAtomicApi::class)

package kotlin.concurrent.atomics

import kotlin.internal.InlineOnly

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicIntArray.updateAt(index: Int, transform: (Int) -> Int): Unit {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicIntArray.updateAndFetchAt(index: Int, transform: (Int) -> Int): Int {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return new }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicIntArray.fetchAndUpdateAt(index: Int, transform: (Int) -> Int): Int {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return old }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLongArray.updateAt(index: Int, transform: (Long) -> Long): Unit {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLongArray.updateAndFetchAt(index: Int, transform: (Long) -> Long): Long {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return new }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun AtomicLongArray.fetchAndUpdateAt(index: Int, transform: (Long) -> Long): Long {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return old }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicArray<T>.updateAt(index: Int, transform: (T) -> T): Unit {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicArray<T>.updateAndFetchAt(index: Int, transform: (T) -> T): T {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return new }
}

@SinceKotlin("2.2")
@ExperimentalAtomicApi
@InlineOnly
public actual inline fun <T> AtomicArray<T>.fetchAndUpdateAt(index: Int, transform: (T) -> T): T {
    while (true) { val old = loadAt(index); val new = transform(old); if (compareAndSetAt(index, old, new)) return old }
}
