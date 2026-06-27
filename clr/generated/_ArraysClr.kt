/*
 * Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual` declarations of _ArraysJvm.kt.
// Bodies are `TODO` pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.collections

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.elementAt(index: Int): T = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.elementAt(index: Int): Byte = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.elementAt(index: Int): Short = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun IntArray.elementAt(index: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun LongArray.elementAt(index: Int): Long = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.elementAt(index: Int): Float = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.elementAt(index: Int): Double = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.elementAt(index: Int): Boolean = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun CharArray.elementAt(index: Int): Char = TODO("clr binding should be implemented")

public actual fun <T> Array<out T>.asList(): List<T> = TODO("clr binding should be implemented")

public actual fun ByteArray.asList(): List<Byte> = TODO("clr binding should be implemented")

public actual fun ShortArray.asList(): List<Short> = TODO("clr binding should be implemented")

public actual fun IntArray.asList(): List<Int> = TODO("clr binding should be implemented")

public actual fun LongArray.asList(): List<Long> = TODO("clr binding should be implemented")

public actual fun FloatArray.asList(): List<Float> = TODO("clr binding should be implemented")

public actual fun DoubleArray.asList(): List<Double> = TODO("clr binding should be implemented")

public actual fun BooleanArray.asList(): List<Boolean> = TODO("clr binding should be implemented")

public actual fun CharArray.asList(): List<Char> = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>.contentDeepEquals(other: Array<out T>): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>?.contentDeepEquals(other: Array<out T>?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>?.contentEquals(other: Array<out T>?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun ByteArray?.contentEquals(other: ByteArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun ShortArray?.contentEquals(other: ShortArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun IntArray?.contentEquals(other: IntArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun LongArray?.contentEquals(other: LongArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun FloatArray?.contentEquals(other: FloatArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun DoubleArray?.contentEquals(other: DoubleArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun BooleanArray?.contentEquals(other: BooleanArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun CharArray?.contentEquals(other: CharArray?): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ByteArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ShortArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun IntArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun LongArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun FloatArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun DoubleArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun BooleanArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun CharArray?.contentHashCode(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ByteArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ShortArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun IntArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun LongArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun FloatArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun DoubleArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun BooleanArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun CharArray?.contentToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.copyInto(destination: Array<T>, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): Array<T> = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.copyInto(destination: ByteArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ByteArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.copyInto(destination: ShortArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ShortArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.copyInto(destination: IntArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): IntArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.copyInto(destination: LongArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): LongArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.copyInto(destination: FloatArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): FloatArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.copyInto(destination: DoubleArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): DoubleArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.copyInto(destination: BooleanArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): BooleanArray = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.copyInto(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): CharArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.copyOf(): Array<T> = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOf(): ByteArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOf(): ShortArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOf(): IntArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOf(): LongArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOf(): FloatArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOf(): DoubleArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOf(): BooleanArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOf(): CharArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOf(newSize: Int): ByteArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOf(newSize: Int): ShortArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOf(newSize: Int): IntArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOf(newSize: Int): LongArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOf(newSize: Int): FloatArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOf(newSize: Int): DoubleArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOf(newSize: Int): BooleanArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOf(newSize: Int): CharArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.copyOf(newSize: Int): Array<T?> = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.copyOfRange(fromIndex: Int, toIndex: Int): Array<T> = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOfRange(fromIndex: Int, toIndex: Int): ByteArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOfRange(fromIndex: Int, toIndex: Int): ShortArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOfRange(fromIndex: Int, toIndex: Int): IntArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOfRange(fromIndex: Int, toIndex: Int): LongArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOfRange(fromIndex: Int, toIndex: Int): FloatArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOfRange(fromIndex: Int, toIndex: Int): DoubleArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOfRange(fromIndex: Int, toIndex: Int): BooleanArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOfRange(fromIndex: Int, toIndex: Int): CharArray = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<T>.fill(element: T, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.fill(element: Byte, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.fill(element: Short, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.fill(element: Int, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.fill(element: Long, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.fill(element: Float, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.fill(element: Double, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.fill(element: Boolean, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.fill(element: Char, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

public actual operator fun <T> Array<T>.plus(element: T): Array<T> = TODO("clr binding should be implemented")

public actual operator fun ByteArray.plus(element: Byte): ByteArray = TODO("clr binding should be implemented")

public actual operator fun ShortArray.plus(element: Short): ShortArray = TODO("clr binding should be implemented")

public actual operator fun IntArray.plus(element: Int): IntArray = TODO("clr binding should be implemented")

public actual operator fun LongArray.plus(element: Long): LongArray = TODO("clr binding should be implemented")

public actual operator fun FloatArray.plus(element: Float): FloatArray = TODO("clr binding should be implemented")

public actual operator fun DoubleArray.plus(element: Double): DoubleArray = TODO("clr binding should be implemented")

public actual operator fun BooleanArray.plus(element: Boolean): BooleanArray = TODO("clr binding should be implemented")

public actual operator fun CharArray.plus(element: Char): CharArray = TODO("clr binding should be implemented")

public actual operator fun <T> Array<T>.plus(elements: Collection<T>): Array<T> = TODO("clr binding should be implemented")

public actual operator fun ByteArray.plus(elements: Collection<Byte>): ByteArray = TODO("clr binding should be implemented")

public actual operator fun ShortArray.plus(elements: Collection<Short>): ShortArray = TODO("clr binding should be implemented")

public actual operator fun IntArray.plus(elements: Collection<Int>): IntArray = TODO("clr binding should be implemented")

public actual operator fun LongArray.plus(elements: Collection<Long>): LongArray = TODO("clr binding should be implemented")

public actual operator fun FloatArray.plus(elements: Collection<Float>): FloatArray = TODO("clr binding should be implemented")

public actual operator fun DoubleArray.plus(elements: Collection<Double>): DoubleArray = TODO("clr binding should be implemented")

public actual operator fun BooleanArray.plus(elements: Collection<Boolean>): BooleanArray = TODO("clr binding should be implemented")

public actual operator fun CharArray.plus(elements: Collection<Char>): CharArray = TODO("clr binding should be implemented")

public actual operator fun <T> Array<T>.plus(elements: Array<out T>): Array<T> = TODO("clr binding should be implemented")

public actual operator fun ByteArray.plus(elements: ByteArray): ByteArray = TODO("clr binding should be implemented")

public actual operator fun ShortArray.plus(elements: ShortArray): ShortArray = TODO("clr binding should be implemented")

public actual operator fun IntArray.plus(elements: IntArray): IntArray = TODO("clr binding should be implemented")

public actual operator fun LongArray.plus(elements: LongArray): LongArray = TODO("clr binding should be implemented")

public actual operator fun FloatArray.plus(elements: FloatArray): FloatArray = TODO("clr binding should be implemented")

public actual operator fun DoubleArray.plus(elements: DoubleArray): DoubleArray = TODO("clr binding should be implemented")

public actual operator fun BooleanArray.plus(elements: BooleanArray): BooleanArray = TODO("clr binding should be implemented")

public actual operator fun CharArray.plus(elements: CharArray): CharArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.plusElement(element: T): Array<T> = TODO("clr binding should be implemented")

public actual fun IntArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun LongArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun ByteArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun ShortArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun DoubleArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun FloatArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun CharArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.internal.InlineOnly
public actual inline fun <T : Comparable<T>> Array<out T>.sort(): Unit { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T : Comparable<T>> Array<out T>.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>): Unit { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>, fromIndex: Int = 0, toIndex: Int = size): Unit { TODO("clr binding should be implemented") }

public actual fun ByteArray.toTypedArray(): Array<Byte> = TODO("clr binding should be implemented")

public actual fun ShortArray.toTypedArray(): Array<Short> = TODO("clr binding should be implemented")

public actual fun IntArray.toTypedArray(): Array<Int> = TODO("clr binding should be implemented")

public actual fun LongArray.toTypedArray(): Array<Long> = TODO("clr binding should be implemented")

public actual fun FloatArray.toTypedArray(): Array<Float> = TODO("clr binding should be implemented")

public actual fun DoubleArray.toTypedArray(): Array<Double> = TODO("clr binding should be implemented")

public actual fun BooleanArray.toTypedArray(): Array<Boolean> = TODO("clr binding should be implemented")

public actual fun CharArray.toTypedArray(): Array<Char> = TODO("clr binding should be implemented")
