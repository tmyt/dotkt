/*
 * Copyright 2010-2025 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "NON_ABSTRACT_FUNCTION_WITH_NO_BODY",
    "MUST_BE_INITIALIZED_OR_BE_ABSTRACT",
)

// CLR atomic arrays. Mirrors the scalar atomics (builtins/Atomics.kt): the genuinely-atomic element ops
// (exchangeAt/compareAndSetAt/compareAndExchangeAt/fetchAndAddAt/addAndFetchAt) are made atomic with a per-instance
// monitor (System.Threading.Monitor.Enter/Exit, reused from Atomics.kt since both live in this package). Correct
// (mutual exclusion), though not lock-free. Every critical section does a backing-array `array[index]` access whose
// bounds check THROWS IndexOutOfBoundsException before the assignment completes — so each section MUST release the
// monitor in a `finally` (a bare enter/exit would leak the lock on an out-of-range index and deadlock every later op
// on the instance: the reentrant monitor's hold count would never drop). loadAt/storeAt take the lock too for
// consistency (a single aligned element access would be fine without it).

package kotlin.concurrent.atomics

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicIntArray {
    private val array: IntArray
    private val lock: Any = Any()

    public actual constructor(size: Int) { array = IntArray(size) }

    // Defensive copy (expect KDoc: "filled with elements of the given array"; JVM/Native copy too). Aliasing would hand
    // the caller an unsynchronized side door into the monitor-guarded storage, bypassing every atomic op's lock.
    public actual constructor(array: IntArray) { this.array = array.copyOf() }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): Int {
        monitorEnter(lock); try { return array[index] } finally { monitorExit(lock) }
    }

    public actual fun storeAt(index: Int, newValue: Int) {
        monitorEnter(lock); try { array[index] = newValue } finally { monitorExit(lock) }
    }

    public actual fun exchangeAt(index: Int, newValue: Int): Int {
        monitorEnter(lock); try { val old = array[index]; array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: Int, newValue: Int): Boolean {
        monitorEnter(lock); try { val ok = array[index] == expectedValue; if (ok) array[index] = newValue; return ok } finally { monitorExit(lock) }
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Int, newValue: Int): Int {
        monitorEnter(lock); try { val old = array[index]; if (old == expectedValue) array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual fun fetchAndAddAt(index: Int, delta: Int): Int {
        monitorEnter(lock); try { val old = array[index]; array[index] = old + delta; return old } finally { monitorExit(lock) }
    }

    public actual fun addAndFetchAt(index: Int, delta: Int): Int {
        monitorEnter(lock); try { val nv = array[index] + delta; array[index] = nv; return nv } finally { monitorExit(lock) }
    }

    public actual override fun toString(): String = array.contentToString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLongArray {
    private val array: LongArray
    private val lock: Any = Any()

    public actual constructor(size: Int) { array = LongArray(size) }

    // Defensive copy (expect KDoc: "filled with elements of the given array"; JVM/Native copy too) — see AtomicIntArray.
    public actual constructor(array: LongArray) { this.array = array.copyOf() }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): Long {
        monitorEnter(lock); try { return array[index] } finally { monitorExit(lock) }
    }

    public actual fun storeAt(index: Int, newValue: Long) {
        monitorEnter(lock); try { array[index] = newValue } finally { monitorExit(lock) }
    }

    public actual fun exchangeAt(index: Int, newValue: Long): Long {
        monitorEnter(lock); try { val old = array[index]; array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: Long, newValue: Long): Boolean {
        monitorEnter(lock); try { val ok = array[index] == expectedValue; if (ok) array[index] = newValue; return ok } finally { monitorExit(lock) }
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Long, newValue: Long): Long {
        monitorEnter(lock); try { val old = array[index]; if (old == expectedValue) array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual fun fetchAndAddAt(index: Int, delta: Long): Long {
        monitorEnter(lock); try { val old = array[index]; array[index] = old + delta; return old } finally { monitorExit(lock) }
    }

    public actual fun addAndFetchAt(index: Int, delta: Long): Long {
        monitorEnter(lock); try { val nv = array[index] + delta; array[index] = nv; return nv } finally { monitorExit(lock) }
    }

    public actual override fun toString(): String = array.contentToString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicArray<T> {
    private val array: Array<T>
    private val lock: Any = Any()

    // Defensive copy (expect KDoc: "filled with elements of the given array"; JVM/Native copy too) — see AtomicIntArray.
    public actual constructor (array: Array<T>) { this.array = array.copyOf() }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): T {
        monitorEnter(lock); try { return array[index] } finally { monitorExit(lock) }
    }

    public actual fun storeAt(index: Int, newValue: T) {
        monitorEnter(lock); try { array[index] = newValue } finally { monitorExit(lock) }
    }

    public actual fun exchangeAt(index: Int, newValue: T): T {
        monitorEnter(lock); try { val old = array[index]; array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: T, newValue: T): Boolean {
        monitorEnter(lock); try { val ok = array[index] === expectedValue; if (ok) array[index] = newValue; return ok } finally { monitorExit(lock) }
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: T, newValue: T): T {
        monitorEnter(lock); try { val old = array[index]; if (old === expectedValue) array[index] = newValue; return old } finally { monitorExit(lock) }
    }

    public actual override fun toString(): String = array.contentToString()
}
