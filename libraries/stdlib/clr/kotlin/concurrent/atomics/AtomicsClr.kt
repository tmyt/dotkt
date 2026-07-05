// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// The genuinely-atomic ops are CAS loops over the class's own compareAndSet (see builtins/Atomics.kt).
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
@file:OptIn(ExperimentalAtomicApi::class)

package kotlin.concurrent.atomics

import kotlin.internal.InlineOnly

// System.Threading.Monitor lock helpers (@ClrIntrinsic-bound). They take an OBJECT, so no byref is
// needed. Declared HERE (a normal, non-builtin source file) rather than in builtins/Atomics.kt so
// they carry real bytecode in the frontend jar: a NON-suppressed body (SynchronizedLazyImpl, in
// kotlin/util/LazyClr.kt) calls them, and the JVM backend cannot codegen a call to a no-bytecode
// builtin. Callers in builtins/Atomics.kt + builtins/AtomicArrays.kt are same-package (no import).
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
internal fun monitorEnter(lock: Any): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
internal fun monitorExit(lock: Any): Unit = TODO("clr binding should be implemented")

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
