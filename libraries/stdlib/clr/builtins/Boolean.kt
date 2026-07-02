/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Represents a value which is either `true` or `false`.
 */
@kotlin.clr.ClrTypeAlias("System.Boolean")
public actual class Boolean private constructor() : Comparable<Boolean> {
    @SinceKotlin("1.3")
    public actual companion object {}

    /** Returns the inverse of this boolean. */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun not(): Boolean = TODO("clr binding should be implemented")

    /**
     * Performs a logical `and` operation between this Boolean and the [other] one. Unlike the `&&` operator,
     * this function does not perform short-circuit evaluation. Both `this` and [other] will always be evaluated.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual infix fun and(other: Boolean): Boolean = TODO("clr binding should be implemented")

    /**
     * Performs a logical `or` operation between this Boolean and the [other] one. Unlike the `||` operator,
     * this function does not perform short-circuit evaluation. Both `this` and [other] will always be evaluated.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual infix fun or(other: Boolean): Boolean = TODO("clr binding should be implemented")

    /** Performs a logical `xor` operation between this Boolean and the [other] one. */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual infix fun xor(other: Boolean): Boolean = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun compareTo(other: Boolean): Int = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun toString(): String = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    public actual override fun hashCode(): Int = TODO("clr binding should be implemented")
}
