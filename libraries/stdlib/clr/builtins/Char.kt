/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Represents a 16-bit Unicode character.
 */
@kotlin.clr.ClrTypeAlias("System.Char")
public actual class Char private constructor() : Comparable<Char> {
    /**
     * Compares this value with the specified value for order.
     */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun compareTo(other: Char): Int = TODO("clr binding should be implemented")

    /** Adds the other Int value to this value resulting a Char. */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun plus(other: Int): Char = TODO("clr binding should be implemented")

    /** Subtracts the other Char value from this value resulting an Int. */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun minus(other: Char): Int = TODO("clr binding should be implemented")

    /** Subtracts the other Int value from this value resulting a Char. */
    @kotlin.internal.IntrinsicConstEvaluation
    public actual operator fun minus(other: Int): Char = TODO("clr binding should be implemented")

    /**
     * Returns this value incremented by one.
     *
     * @sample samples.misc.Builtins.inc
     */
    public actual operator fun inc(): Char = TODO("clr binding should be implemented")

    /**
     * Returns this value decremented by one.
     *
     * @sample samples.misc.Builtins.dec
     */
    public actual operator fun dec(): Char = TODO("clr binding should be implemented")

    /** Creates a range from this value to the specified [other] value. */
    public actual operator fun rangeTo(other: Char): CharRange = TODO("clr binding should be implemented")

    /**
     * Creates a range from this value up to but excluding the specified [other] value.
     *
     * If the [other] value is less than or equal to `this` value, then the returned range is empty.
     */
    @SinceKotlin("1.9")
    @WasExperimental(ExperimentalStdlibApi::class)
    public actual operator fun rangeUntil(other: Char): CharRange = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Byte`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code.toByte()"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toByte(): Byte = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Char`. */
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toChar(): Char = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Short`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code.toShort()"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toShort(): Short = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Int`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toInt(): Int = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Long`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code.toLong()"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toLong(): Long = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Float`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code.toFloat()"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toFloat(): Float = TODO("clr binding should be implemented")

    /** Returns the value of this character as a `Double`. */
    @Deprecated("Conversion of Char to Number is deprecated. Use Char.code property instead.", ReplaceWith("this.code.toDouble()"))
    @DeprecatedSinceKotlin(warningSince = "1.5")
    @kotlin.internal.IntrinsicConstEvaluation
    @kotlin.clr.ClrConv
    public actual fun toDouble(): Double = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun toString(): String = TODO("clr binding should be implemented")

    @kotlin.internal.IntrinsicConstEvaluation
    public actual override fun equals(other: Any?): Boolean = TODO("clr binding should be implemented")

    public actual override fun hashCode(): Int = TODO("clr binding should be implemented")

    public actual companion object {
        /**
         * The minimum value of a character code unit.
         */
        @SinceKotlin("1.3")
        public actual const val MIN_VALUE: Char = '\u0000'

        /**
         * The maximum value of a character code unit.
         */
        @SinceKotlin("1.3")
        public actual const val MAX_VALUE: Char = '\uFFFF'

        /**
         * The minimum value of a Unicode high-surrogate code unit.
         */
        public actual const val MIN_HIGH_SURROGATE: Char = '\uD800'

        /**
         * The maximum value of a Unicode high-surrogate code unit.
         */
        public actual const val MAX_HIGH_SURROGATE: Char = '\uDBFF'

        /**
         * The minimum value of a Unicode low-surrogate code unit.
         */
        public actual const val MIN_LOW_SURROGATE: Char = '\uDC00'

        /**
         * The maximum value of a Unicode low-surrogate code unit.
         */
        public actual const val MAX_LOW_SURROGATE: Char = '\uDFFF'

        /**
         * The minimum value of a Unicode surrogate code unit.
         */
        public actual const val MIN_SURROGATE: Char = MIN_HIGH_SURROGATE

        /**
         * The maximum value of a Unicode surrogate code unit.
         */
        public actual const val MAX_SURROGATE: Char = MAX_LOW_SURROGATE

        /**
         * The number of bytes used to represent a Char in a binary form.
         */
        @SinceKotlin("1.3")
        public actual const val SIZE_BYTES: Int = 2

        /**
         * The number of bits used to represent a Char in a binary form.
         */
        @SinceKotlin("1.3")
        public actual const val SIZE_BITS: Int = 16
    }
}
