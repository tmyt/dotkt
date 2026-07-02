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

// CLR atomics. The genuinely-atomic ops (exchange/compareAndSet/compareAndExchange/fetchAndAdd/addAndFetch) are made
// atomic with a per-instance monitor (System.Threading.Monitor.Enter/Exit, bound via @Clr — they take an OBJECT, so no
// byref is needed, unlike Interlocked which needs a managed pointer to the field). Correct (mutual exclusion), though
// not lock-free; the critical sections do only a field read/compare/assign, which never throw, so a plain enter/exit
// (no try/finally) is safe.

package kotlin.concurrent.atomics

import kotlin.clr.ClrRefArgument

// Interlocked byref helpers: each takes its first argument BY REFERENCE (@ClrRefArgument on a PLAIN param, NOT a
// `ClrRef<T>` — the stdlib ABI stays identical to the standard Kotlin stdlib), so the atomics can pass their backing
// field directly (`interlockedExchangeInt(v, ..)` -> ldflda) to the BCL `ref`-parameter overloads — a genuine
// lock-free implementation (unlike the Monitor-lock fallback kept for AtomicBoolean/AtomicReference, which have no
// Interlocked overload). bir2cir reads @ClrIntrinsic + @ClrRefArgument from the ref.dll and substitutes the call to the
// BCL static, carrying the byref-ness into the resolved overload + the `ldflda` address-load.
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.Exchange")
internal fun interlockedExchangeInt(@ClrRefArgument location: Int, value: Int): Int = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.CompareExchange")
internal fun interlockedCompareExchangeInt(@ClrRefArgument location: Int, value: Int, comparand: Int): Int = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.Add")
internal fun interlockedAddInt(@ClrRefArgument location: Int, value: Int): Int = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.Exchange")
internal fun interlockedExchangeLong(@ClrRefArgument location: Long, value: Long): Long = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.CompareExchange")
internal fun interlockedCompareExchangeLong(@ClrRefArgument location: Long, value: Long, comparand: Long): Long = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Interlocked.Add")
internal fun interlockedAddLong(@ClrRefArgument location: Long, value: Long): Long = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
internal fun monitorEnter(lock: Any): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
internal fun monitorExit(lock: Any): Unit = TODO("clr binding should be implemented")

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicInt actual constructor(value: Int) {
    private var v: Int = value

    public actual fun load(): Int = v

    public actual fun store(newValue: Int) { v = newValue }

    // Lock-free: the @ClrRefArgument param passes a managed pointer to the backing field to Interlocked's `ref` param.
    public actual fun exchange(newValue: Int): Int = interlockedExchangeInt(v, newValue)

    public actual fun compareAndSet(expectedValue: Int, newValue: Int): Boolean =
        interlockedCompareExchangeInt(v, newValue, expectedValue) == expectedValue

    public actual fun compareAndExchange(expectedValue: Int, newValue: Int): Int =
        interlockedCompareExchangeInt(v, newValue, expectedValue)

    // Interlocked.Add returns the NEW value; fetchAndAdd wants the OLD (subtract back the delta).
    public actual fun fetchAndAdd(delta: Int): Int = interlockedAddInt(v, delta) - delta

    public actual fun addAndFetch(delta: Int): Int = interlockedAddInt(v, delta)

    public actual override fun toString(): String = v.toString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLong actual constructor(value: Long) {
    private var v: Long = value

    public actual fun load(): Long = v

    public actual fun store(newValue: Long) { v = newValue }

    public actual fun exchange(newValue: Long): Long = interlockedExchangeLong(v, newValue)

    public actual fun compareAndSet(expectedValue: Long, newValue: Long): Boolean =
        interlockedCompareExchangeLong(v, newValue, expectedValue) == expectedValue

    public actual fun compareAndExchange(expectedValue: Long, newValue: Long): Long =
        interlockedCompareExchangeLong(v, newValue, expectedValue)

    public actual fun fetchAndAdd(delta: Long): Long = interlockedAddLong(v, delta) - delta

    public actual fun addAndFetch(delta: Long): Long = interlockedAddLong(v, delta)

    public actual override fun toString(): String = v.toString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicBoolean actual constructor(value: Boolean) {
    private var v: Boolean = value
    private val lock: Any = Any()

    public actual fun load(): Boolean = v

    public actual fun store(newValue: Boolean) { v = newValue }

    public actual fun exchange(newValue: Boolean): Boolean {
        monitorEnter(lock); val old = v; v = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSet(expectedValue: Boolean, newValue: Boolean): Boolean {
        monitorEnter(lock); val ok = v == expectedValue; if (ok) v = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchange(expectedValue: Boolean, newValue: Boolean): Boolean {
        monitorEnter(lock); val old = v; if (old == expectedValue) v = newValue; monitorExit(lock); return old
    }

    public actual override fun toString(): String = v.toString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicReference<T> actual constructor(value: T) {
    private var v: T = value
    private val lock: Any = Any()

    public actual fun load(): T = v

    public actual fun store(newValue: T) { v = newValue }

    public actual fun exchange(newValue: T): T {
        monitorEnter(lock); val old = v; v = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSet(expectedValue: T, newValue: T): Boolean {
        monitorEnter(lock); val ok = v === expectedValue; if (ok) v = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchange(expectedValue: T, newValue: T): T {
        monitorEnter(lock); val old = v; if (old === expectedValue) v = newValue; monitorExit(lock); return old
    }

    public actual override fun toString(): String = v.toString()
}
