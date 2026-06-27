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

// Step-1 CLR stub mirroring the JVM `actual` declarations.
// Bodies are TODO pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.concurrent.atomics

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicInt actual constructor(value: Int) {
    public actual fun load(): Int = TODO("clr binding should be implemented")

    public actual fun store(newValue: Int) { TODO("clr binding should be implemented") }

    public actual fun exchange(newValue: Int): Int = TODO("clr binding should be implemented")

    public actual fun compareAndSet(expectedValue: Int, newValue: Int): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchange(expectedValue: Int, newValue: Int): Int = TODO("clr binding should be implemented")

    public actual fun fetchAndAdd(delta: Int): Int = TODO("clr binding should be implemented")

    public actual fun addAndFetch(delta: Int): Int = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLong actual constructor(value: Long) {
    public actual fun load(): Long = TODO("clr binding should be implemented")

    public actual fun store(newValue: Long) { TODO("clr binding should be implemented") }

    public actual fun exchange(newValue: Long): Long = TODO("clr binding should be implemented")

    public actual fun compareAndSet(expectedValue: Long, newValue: Long): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchange(expectedValue: Long, newValue: Long): Long = TODO("clr binding should be implemented")

    public actual fun fetchAndAdd(delta: Long): Long = TODO("clr binding should be implemented")

    public actual fun addAndFetch(delta: Long): Long = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicBoolean actual constructor(value: Boolean) {
    public actual fun load(): Boolean = TODO("clr binding should be implemented")

    public actual fun store(newValue: Boolean) { TODO("clr binding should be implemented") }

    public actual fun exchange(newValue: Boolean): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndSet(expectedValue: Boolean, newValue: Boolean): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchange(expectedValue: Boolean, newValue: Boolean): Boolean = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicReference<T> actual constructor(value: T) {
    public actual fun load(): T = TODO("clr binding should be implemented")

    public actual fun store(newValue: T) { TODO("clr binding should be implemented") }

    public actual fun exchange(newValue: T): T = TODO("clr binding should be implemented")

    public actual fun compareAndSet(expectedValue: T, newValue: T): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchange(expectedValue: T, newValue: T): T = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}
