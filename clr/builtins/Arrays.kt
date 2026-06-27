/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "NON_ABSTRACT_FUNCTION_WITH_NO_BODY",
    "PRIMARY_CONSTRUCTOR_DELEGATION_CALL_EXPECTED",
    "MUST_BE_INITIALIZED_OR_BE_ABSTRACT",
)

// Step-1 CLR stub mirroring the JVM `actual` declarations.
// Bodies are TODO pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

public actual class ByteArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Byte)

    public actual operator fun get(index: Int): Byte = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Byte): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): ByteIterator = TODO("clr binding should be implemented")
}

public actual class CharArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Char)

    public actual operator fun get(index: Int): Char = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Char): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): CharIterator = TODO("clr binding should be implemented")
}

public actual class ShortArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Short)

    public actual operator fun get(index: Int): Short = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Short): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): ShortIterator = TODO("clr binding should be implemented")
}

public actual class IntArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Int)

    public actual operator fun get(index: Int): Int = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Int): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): IntIterator = TODO("clr binding should be implemented")
}

public actual class LongArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Long)

    public actual operator fun get(index: Int): Long = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Long): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): LongIterator = TODO("clr binding should be implemented")
}

public actual class FloatArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Float)

    public actual operator fun get(index: Int): Float = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Float): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): FloatIterator = TODO("clr binding should be implemented")
}

public actual class DoubleArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Double)

    public actual operator fun get(index: Int): Double = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Double): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): DoubleIterator = TODO("clr binding should be implemented")
}

public actual class BooleanArray
public actual constructor(size: Int) {
    @Suppress("WRONG_MODIFIER_TARGET")
    public actual inline constructor(size: Int, init: (Int) -> Boolean)

    public actual operator fun get(index: Int): Boolean = TODO("clr binding should be implemented")

    public actual operator fun set(index: Int, value: Boolean): Unit { TODO("clr binding should be implemented") }

    public actual val size: Int get() = TODO("clr binding should be implemented")

    public actual operator fun iterator(): BooleanIterator = TODO("clr binding should be implemented")
}
