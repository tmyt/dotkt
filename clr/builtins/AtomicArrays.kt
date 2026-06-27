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
public actual class AtomicIntArray {
    public actual constructor(size: Int)

    public actual constructor(array: IntArray)

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual fun loadAt(index: Int): Int = TODO("clr binding should be implemented")

    public actual fun storeAt(index: Int, newValue: Int) { TODO("clr binding should be implemented") }

    public actual fun exchangeAt(index: Int, newValue: Int): Int = TODO("clr binding should be implemented")

    public actual fun compareAndSetAt(index: Int, expectedValue: Int, newValue: Int): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Int, newValue: Int): Int = TODO("clr binding should be implemented")

    public actual fun fetchAndAddAt(index: Int, delta: Int): Int = TODO("clr binding should be implemented")

    public actual fun addAndFetchAt(index: Int, delta: Int): Int = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicLongArray {
    public actual constructor(size: Int)

    public actual constructor(array: LongArray)

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual fun loadAt(index: Int): Long = TODO("clr binding should be implemented")

    public actual fun storeAt(index: Int, newValue: Long) { TODO("clr binding should be implemented") }

    public actual fun exchangeAt(index: Int, newValue: Long): Long = TODO("clr binding should be implemented")

    public actual fun compareAndSetAt(index: Int, expectedValue: Long, newValue: Long): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchangeAt(index: Int, expectedValue: Long, newValue: Long): Long = TODO("clr binding should be implemented")

    public actual fun fetchAndAddAt(index: Int, delta: Long): Long = TODO("clr binding should be implemented")

    public actual fun addAndFetchAt(index: Int, delta: Long): Long = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}

@SinceKotlin("2.1")
@ExperimentalAtomicApi
public actual class AtomicArray<T> {
    public actual constructor (array: Array<T>)

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual fun loadAt(index: Int): T = TODO("clr binding should be implemented")

    public actual fun storeAt(index: Int, newValue: T) { TODO("clr binding should be implemented") }

    public actual fun exchangeAt(index: Int, newValue: T): T = TODO("clr binding should be implemented")

    public actual fun compareAndSetAt(index: Int, expectedValue: T, newValue: T): Boolean = TODO("clr binding should be implemented")

    public actual fun compareAndExchangeAt(index: Int, expectedValue: T, newValue: T): T = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}
