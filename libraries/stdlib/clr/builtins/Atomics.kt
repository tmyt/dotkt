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

// CLR atomics. AtomicInt/AtomicLong RMW ops (exchange/compareAndSet/compareAndExchange/fetchAndAdd/addAndFetch) are
// lock-free via Interlocked (byref to the backing field); their load()/store() bind Volatile.Read/Write (ordered +
// non-tearing), and a Volatile store vs an Interlocked RMW are mutually hardware-atomic — linearizable.
//
// AtomicBoolean/AtomicReference RMW ops have no Interlocked overload, so they use a per-instance monitor
// (System.Threading.Monitor.Enter/Exit, @Clr-bound — Monitor takes an OBJECT, so no byref). For those, a software-CAS
// linearizes only if EVERY write to the cell goes through the same mechanism: so store() ALSO takes the monitor (a
// lock-free store would slip inside the monitor's read-modify-write gap and be lost). The backing field is `@Volatile`
// so load() stays an unlocked acquire read (a single reference/bool read linearizes at the read instant). The monitor
// RMW sections do only a field read/compare/assign (no bounds check, unlike the array atomics), which never throws, so
// a plain enter/exit (no try/finally) is safe.

package kotlin.concurrent.atomics

import kotlin.clr.ClrRefArgument
import kotlin.concurrent.Volatile

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

// Volatile load/store helpers for the LOCK-FREE scalar atomics (AtomicInt/AtomicLong): each takes its location BY
// REFERENCE (@ClrRefArgument, same `ldflda` pattern as the Interlocked helpers) and binds to the non-generic
// System.Threading.Volatile.Read/Write overload for that primitive. These give load()/store() the acquire/release
// ordering the atomicfu/kotlinx contract requires — a plain field read/write has NO ordering guarantee, and a plain
// 64-bit read/write can TEAR on a 32-bit runtime; Volatile.Read/Write on the field address is ordered AND atomic
// (Volatile.Read(ref long) is atomic on every platform). Their RMW ops are lock-free (Interlocked), so a Volatile
// store and an Interlocked RMW are mutually hardware-atomic on the same cell — linearizable.
//
// NOTE the LOCK-EMULATED atomics (AtomicBoolean/AtomicReference) do NOT use these: their RMW ops use a monitor (no
// Interlocked overload), so a lock-free store would race the monitor's read-modify-write gap and be lost. There,
// store() takes the SAME monitor (mutual exclusion) and the backing field is `@Volatile` so load() stays an unlocked
// acquire read (a single reference/bool read linearizes at the read; only writes must share the mechanism).
@kotlin.clr.ClrIntrinsic("System.Threading.Volatile.Read")
internal fun volatileReadInt(@ClrRefArgument location: Int): Int = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Volatile.Write")
internal fun volatileWriteInt(@ClrRefArgument location: Int, value: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Volatile.Read")
internal fun volatileReadLong(@ClrRefArgument location: Long): Long = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Threading.Volatile.Write")
internal fun volatileWriteLong(@ClrRefArgument location: Long, value: Long): Unit = TODO("clr binding should be implemented")
// The object-CAS helper (interlockedCompareExchangeRef) lives in kotlin/concurrent/atomics/AtomicsClr.kt, NOT here:
// SafeContinuation (a normally-codegen'd body) calls it, and the JVM backend cannot codegen a call to a no-bytecode
// builtin (same reason monitorEnter/monitorExit moved there). The value-typed helpers above are called only from the
// builtin atomic bodies in THIS file, so they stay builtins.

// monitorEnter/monitorExit (the System.Threading.Monitor lock helpers, @ClrIntrinsic-bound) used to
// live here, but this file is staged as a builtin (no-bytecode) in the frontend-jar build, so a call
// to them from any NORMALLY-codegen'd body (e.g. SynchronizedLazyImpl) aborts the JVM backend with
// "unhandled intrinsic". They now live in the NON-builtin kotlin/concurrent/atomics/AtomicsClr.kt so
// they carry real jar bytecode; same package, so the callers here still resolve them without an import.

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicInt actual constructor(value: Int) {
    private var v: Int = value

    // Volatile (acquire) read / (release) write of the backing field — not a plain field access.
    public actual fun load(): Int = volatileReadInt(v)

    public actual fun store(newValue: Int) { volatileWriteInt(v, newValue) }

    // Lock-free: the @ClrRefArgument param passes a managed pointer to the backing field to Interlocked's `ref` param.
    public actual fun exchange(newValue: Int): Int = interlockedExchangeInt(v, newValue)

    public actual fun compareAndSet(expectedValue: Int, newValue: Int): Boolean =
        interlockedCompareExchangeInt(v, newValue, expectedValue) == expectedValue

    public actual fun compareAndExchange(expectedValue: Int, newValue: Int): Int =
        interlockedCompareExchangeInt(v, newValue, expectedValue)

    // Interlocked.Add returns the NEW value; fetchAndAdd wants the OLD (subtract back the delta).
    public actual fun fetchAndAdd(delta: Int): Int = interlockedAddInt(v, delta) - delta

    public actual fun addAndFetch(delta: Int): Int = interlockedAddInt(v, delta)

    public actual override fun toString(): String = load().toString()   // volatile read, not a plain field access
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLong actual constructor(value: Long) {
    private var v: Long = value

    // Volatile read/write — ordered AND atomic (Volatile.Read(ref long) never tears, even on a 32-bit runtime).
    public actual fun load(): Long = volatileReadLong(v)

    public actual fun store(newValue: Long) { volatileWriteLong(v, newValue) }

    public actual fun exchange(newValue: Long): Long = interlockedExchangeLong(v, newValue)

    public actual fun compareAndSet(expectedValue: Long, newValue: Long): Boolean =
        interlockedCompareExchangeLong(v, newValue, expectedValue) == expectedValue

    public actual fun compareAndExchange(expectedValue: Long, newValue: Long): Long =
        interlockedCompareExchangeLong(v, newValue, expectedValue)

    public actual fun fetchAndAdd(delta: Long): Long = interlockedAddLong(v, delta) - delta

    public actual fun addAndFetch(delta: Long): Long = interlockedAddLong(v, delta)

    // load() (Volatile.Read) — a plain 64-bit `v` read could TEAR on a 32-bit runtime.
    public actual override fun toString(): String = load().toString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicBoolean actual constructor(value: Boolean) {
    // @Volatile: gives load() an unlocked acquire read. The RMW ops (below) are monitor-guarded, so store() must take
    // the SAME monitor — a lock-free store would race the monitor's read-modify-write gap and be lost (non-linearizable).
    @Volatile private var v: Boolean = value
    private val lock: Any = Any()

    public actual fun load(): Boolean = v   // volatile acquire read

    public actual fun store(newValue: Boolean) {
        monitorEnter(lock); try { v = newValue } finally { monitorExit(lock) }
    }

    public actual fun exchange(newValue: Boolean): Boolean {
        monitorEnter(lock); val old = v; v = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSet(expectedValue: Boolean, newValue: Boolean): Boolean {
        monitorEnter(lock); val ok = v == expectedValue; if (ok) v = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchange(expectedValue: Boolean, newValue: Boolean): Boolean {
        monitorEnter(lock); val old = v; if (old == expectedValue) v = newValue; monitorExit(lock); return old
    }

    public actual override fun toString(): String = load().toString()
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicReference<T> actual constructor(value: T) {
    // @Volatile gives load() an unlocked acquire read (a reference read is atomic on the CLR). The RMW ops are monitor-
    // guarded (Volatile has no non-generic object overload to bind lock-free by @ClrRefArgument), so store() must take
    // the SAME monitor — a lock-free store would race the monitor's read-modify-write gap and be lost (non-linearizable).
    @Volatile private var v: T = value
    private val lock: Any = Any()

    public actual fun load(): T = v   // volatile acquire read

    public actual fun store(newValue: T) {
        monitorEnter(lock); try { v = newValue } finally { monitorExit(lock) }
    }

    public actual fun exchange(newValue: T): T {
        monitorEnter(lock); val old = v; v = newValue; monitorExit(lock); return old
    }

    public actual fun compareAndSet(expectedValue: T, newValue: T): Boolean {
        monitorEnter(lock); val ok = v === expectedValue; if (ok) v = newValue; monitorExit(lock); return ok
    }

    public actual fun compareAndExchange(expectedValue: T, newValue: T): T {
        monitorEnter(lock); val old = v; if (old === expectedValue) v = newValue; monitorExit(lock); return old
    }

    public actual override fun toString(): String = load().toString()
}
