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
// (mutual exclusion), though not lock-free; each critical section does only a backing-array read/compare/assign,
// which never throws, so a plain enter/exit (no try/finally) is safe. loadAt/storeAt take the lock too for
// consistency (a single aligned element access would be fine without it).

package kotlin.concurrent.atomics

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicIntArray {
    private val array: IntArray
    private val lock: Any = Any()

    public actual constructor(size: Int) { array = IntArray(size) }

    public actual constructor(array: IntArray) { this.array = array }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): Int {
        monitorEnter(lock); val r = array[index]; monitorExit(lock); return r
    }

    public actual fun storeAt(index: Int, newValue: Int) {
        monitorEnter(lock); array[index] = newValue; monitorExit(lock)
    }

    public actual fun exchangeAt(index: Int, newValue: Int): Int {
        monitorEnter(lock); val old = array[index]; array[index] = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: Int, newValue: Int): Boolean {
        monitorEnter(lock); val ok = array[index] == expectedValue; if (ok) array[index] = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Int, newValue: Int): Int {
        monitorEnter(lock); val old = array[index]; if (old == expectedValue) array[index] = newValue; monitorExit(lock); return old
    }

    public actual fun fetchAndAddAt(index: Int, delta: Int): Int {
        monitorEnter(lock); val old = array[index]; array[index] = old + delta; monitorExit(lock); return old
    }

    public actual fun addAndFetchAt(index: Int, delta: Int): Int {
        monitorEnter(lock); val nv = array[index] + delta; array[index] = nv; monitorExit(lock); return nv
    }

    public actual override fun toString(): String = array.contentToString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLongArray {
    private val array: LongArray
    private val lock: Any = Any()

    public actual constructor(size: Int) { array = LongArray(size) }

    public actual constructor(array: LongArray) { this.array = array }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): Long {
        monitorEnter(lock); val r = array[index]; monitorExit(lock); return r
    }

    public actual fun storeAt(index: Int, newValue: Long) {
        monitorEnter(lock); array[index] = newValue; monitorExit(lock)
    }

    public actual fun exchangeAt(index: Int, newValue: Long): Long {
        monitorEnter(lock); val old = array[index]; array[index] = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: Long, newValue: Long): Boolean {
        monitorEnter(lock); val ok = array[index] == expectedValue; if (ok) array[index] = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Long, newValue: Long): Long {
        monitorEnter(lock); val old = array[index]; if (old == expectedValue) array[index] = newValue; monitorExit(lock); return old
    }

    public actual fun fetchAndAddAt(index: Int, delta: Long): Long {
        monitorEnter(lock); val old = array[index]; array[index] = old + delta; monitorExit(lock); return old
    }

    public actual fun addAndFetchAt(index: Int, delta: Long): Long {
        monitorEnter(lock); val nv = array[index] + delta; array[index] = nv; monitorExit(lock); return nv
    }

    public actual override fun toString(): String = array.contentToString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicArray<T> {
    private val array: Array<T>
    private val lock: Any = Any()

    public actual constructor (array: Array<T>) { this.array = array }

    public actual val size: Int get() = array.size

    public actual fun loadAt(index: Int): T {
        monitorEnter(lock); val r = array[index]; monitorExit(lock); return r
    }

    public actual fun storeAt(index: Int, newValue: T) {
        monitorEnter(lock); array[index] = newValue; monitorExit(lock)
    }

    public actual fun exchangeAt(index: Int, newValue: T): T {
        monitorEnter(lock); val old = array[index]; array[index] = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSetAt(index: Int, expectedValue: T, newValue: T): Boolean {
        monitorEnter(lock); val ok = array[index] === expectedValue; if (ok) array[index] = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchangeAt(index: Int, expectedValue: T, newValue: T): T {
        monitorEnter(lock); val old = array[index]; if (old === expectedValue) array[index] = newValue; monitorExit(lock); return old
    }

    public actual override fun toString(): String = array.contentToString()
}
