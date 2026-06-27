/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * The `String` class represents character strings. All string literals in Kotlin programs, such as `"abc"`, are
 * implemented as instances of this class.
 */
public actual class String : Comparable<String>, CharSequence {
    public actual companion object {}

    /**
     * Returns a string obtained by concatenating this string with the string representation of the given [other] object.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun plus(other: Any?): String = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override val length: Int get() = TODO("clr binding should be implemented")

    /**
     * Returns the character of this string at the specified [index].
     *
     * If the [index] is out of bounds of this string, throws an [IndexOutOfBoundsException].
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun get(index: Int): Char = TODO("clr binding should be implemented")

    public actual override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun compareTo(other: String): Int = TODO("clr binding should be implemented")

    /**
     * Indicates if [other] object is equal to this [String].
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun toString(): String = TODO("clr binding should be implemented")
}
