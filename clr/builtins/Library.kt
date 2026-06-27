/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS", "NON_MEMBER_FUNCTION_NO_BODY", "REIFIED_TYPE_PARAMETER_NO_INLINE")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Returns a string representation of the object. Can be called with a null receiver, in which case
 * it returns the string "null".
 */
public actual fun Any?.toString(): String = TODO("clr binding should be implemented")

/**
 * Concatenates this string with the string representation of the given [other] object. If either the receiver
 * or the [other] object are null, they are represented as the string "null".
 */
public actual operator fun String?.plus(other: Any?): String = TODO("clr binding should be implemented")

/**
 * Returns an array of objects of the given type with the given [size], initialized with null values.
 *
 * @throws RuntimeException if the specified [size] is negative.
 */
public actual fun <reified T> arrayOfNulls(size: Int): Array<T?> = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified elements.
 */
public actual inline fun <reified T> arrayOf(vararg elements: T): Array<T> = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Double] numbers.
 */
public actual fun doubleArrayOf(vararg elements: Double): DoubleArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Float] numbers.
 */
public actual fun floatArrayOf(vararg elements: Float): FloatArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Long] numbers.
 */
public actual fun longArrayOf(vararg elements: Long): LongArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Int] numbers.
 */
public actual fun intArrayOf(vararg elements: Int): IntArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified characters.
 */
public actual fun charArrayOf(vararg elements: Char): CharArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Short] numbers.
 */
public actual fun shortArrayOf(vararg elements: Short): ShortArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified [Byte] numbers.
 */
public actual fun byteArrayOf(vararg elements: Byte): ByteArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing the specified boolean values.
 */
public actual fun booleanArrayOf(vararg elements: Boolean): BooleanArray = TODO("clr binding should be implemented")

/**
 * Returns an array containing enum T entries.
 */
@SinceKotlin("1.1")
public actual inline fun <reified T : Enum<T>> enumValues(): Array<T> = TODO("clr binding should be implemented")

/**
 * Returns an enum entry with specified name.
 */
@SinceKotlin("1.1")
public actual inline fun <reified T : Enum<T>> enumValueOf(name: String): T = TODO("clr binding should be implemented")
