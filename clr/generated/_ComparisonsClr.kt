/*
 * Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual` declarations.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.comparisons

/**
 * Returns the greater of two values.
 *
 * If values are equal, returns the first one.
 */
@SinceKotlin("1.1")
public actual fun <T : Comparable<T>> maxOf(a: T, b: T): T = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Byte, b: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Short, b: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Int, b: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Long, b: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 *
 * If either value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Float, b: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the greater of two values.
 *
 * If either value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Double, b: Double): Double = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 *
 * If there are multiple equal maximal values, returns the first of them.
 */
@SinceKotlin("1.1")
public actual fun <T : Comparable<T>> maxOf(a: T, b: T, c: T): T = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Byte, b: Byte, c: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Short, b: Short, c: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Int, b: Int, c: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Long, b: Long, c: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Float, b: Float, c: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the greater of three values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun maxOf(a: Double, b: Double, c: Double): Double = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 *
 * If there are multiple equal maximal values, returns the first of them.
 */
@SinceKotlin("1.4")
public actual fun <T : Comparable<T>> maxOf(a: T, vararg other: T): T = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Byte, vararg other: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Short, vararg other: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Int, vararg other: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Long, vararg other: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Float, vararg other: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the greater of the given values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.4")
public actual fun maxOf(a: Double, vararg other: Double): Double = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 *
 * If values are equal, returns the first one.
 */
@SinceKotlin("1.1")
public actual fun <T : Comparable<T>> minOf(a: T, b: T): T = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Byte, b: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Short, b: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Int, b: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Long, b: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 *
 * If either value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Float, b: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the smaller of two values.
 *
 * If either value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Double, b: Double): Double = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 *
 * If there are multiple equal minimal values, returns the first of them.
 */
@SinceKotlin("1.1")
public actual fun <T : Comparable<T>> minOf(a: T, b: T, c: T): T = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Byte, b: Byte, c: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Short, b: Short, c: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Int, b: Int, c: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Long, b: Long, c: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Float, b: Float, c: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the smaller of three values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun minOf(a: Double, b: Double, c: Double): Double = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 *
 * If there are multiple equal minimal values, returns the first of them.
 */
@SinceKotlin("1.4")
public actual fun <T : Comparable<T>> minOf(a: T, vararg other: T): T = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Byte, vararg other: Byte): Byte = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Short, vararg other: Short): Short = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Int, vararg other: Int): Int = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Long, vararg other: Long): Long = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Float, vararg other: Float): Float = TODO("clr binding should be implemented")

/**
 * Returns the smaller of the given values.
 *
 * If any value is `NaN`, returns `NaN`.
 */
@SinceKotlin("1.4")
public actual fun minOf(a: Double, vararg other: Double): Double = TODO("clr binding should be implemented")
