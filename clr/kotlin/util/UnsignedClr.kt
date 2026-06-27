@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

import kotlin.internal.InlineOnly

@PublishedApi
internal actual fun uintRemainder(v1: UInt, v2: UInt): UInt = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun uintDivide(v1: UInt, v2: UInt): UInt = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun ulongDivide(v1: ULong, v2: ULong): ULong = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun ulongRemainder(v1: ULong, v2: ULong): ULong = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun uintCompare(v1: Int, v2: Int): Int = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun ulongCompare(v1: Long, v2: Long): Int = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun uintToULong(value: Int): ULong = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun uintToLong(value: Int): Long = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun uintToFloat(value: Int): Float = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun floatToUInt(value: Float): UInt = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun uintToDouble(value: Int): Double = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun doubleToUInt(value: Double): UInt = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun ulongToFloat(value: Long): Float = TODO("clr binding should be implemented")

@PublishedApi
@InlineOnly
internal actual inline fun floatToULong(value: Float): ULong = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun ulongToDouble(value: Long): Double = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun doubleToULong(value: Double): ULong = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun uintToString(value: Int): String = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun uintToString(value: Int, base: Int): String = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun ulongToString(value: Long): String = TODO("clr binding should be implemented")

internal actual fun ulongToString(value: Long, base: Int): String = TODO("clr binding should be implemented")
