/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * A generic array of objects.
 * Instances of this class are represented as `T[]`.
 * Array instances can be created using the [arrayOf], [arrayOfNulls] and [emptyArray]
 * standard library functions.
 *
 * See [Kotlin language documentation](https://kotlinlang.org/docs/arrays.html)
 * for more information on arrays.
 */
public actual class Array<T> {
    /**
     * Creates a new array of the specified [size], where each element is calculated by calling the specified
     * [init] function.
     *
     * @throws RuntimeException if the specified [size] is negative.
     */
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> T)

    /**
     * Returns the array element at the given [index].
     *
     * If the [index] is out of bounds of this array, throws an [IndexOutOfBoundsException].
     */
    public actual operator fun get(index: Int): T = TODO("clr binding should be implemented")

    /**
     * Sets the array element at the given [index] to the given [value].
     *
     * If the [index] is out of bounds of this array, throws an [IndexOutOfBoundsException].
     */
    public actual operator fun set(index: Int, value: T): Unit { TODO("clr binding should be implemented") }

    /**
     * Returns the number of elements in the array.
     */
    public actual val size: Int get() = TODO("clr binding should be implemented")

    /** Creates an [Iterator] for iterating over the elements of the array. */
    public actual operator fun iterator(): Iterator<T> = TODO("clr binding should be implemented")
}
