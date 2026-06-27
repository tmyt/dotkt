/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * The root of the Kotlin class hierarchy. Every Kotlin class has [Any] as a superclass.
 */
public actual open class Any {
    /**
     * Indicates whether some other object is "equal to" this one.
     */
    public actual open operator fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    /**
     * Returns a hash code value for the object.
     */
    public actual open fun hashCode(): Int = TODO("clr binding should be implemented")

    /**
     * Returns a string representation of the object.
     */
    public actual open fun toString(): String = TODO("clr binding should be implemented")
}
