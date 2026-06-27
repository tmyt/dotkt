/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * The common base class of all enum classes.
 * See the [Kotlin language documentation](https://kotlinlang.org/docs/reference/enum-classes.html) for more
 * information on enum classes.
 */
public actual abstract class Enum<E : Enum<E>> actual constructor(name: String, ordinal: Int) : Comparable<E> {
    public actual companion object {}

    /**
     * Returns the name of this enum constant, exactly as declared in its enum declaration.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual final val name: String get() = TODO("clr binding should be implemented")

    /**
     * Returns the ordinal of this enumeration constant (its position in its enum declaration, where the initial constant
     * is assigned an ordinal of zero).
     */
    public actual final val ordinal: Int get() = TODO("clr binding should be implemented")

    public actual override final fun compareTo(other: E): Int = TODO("clr binding should be implemented")

    public actual override final fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    public actual override final fun hashCode(): Int = TODO("clr binding should be implemented")

    public actual override fun toString(): String = TODO("clr binding should be implemented")
}
