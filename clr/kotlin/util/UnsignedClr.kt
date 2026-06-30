@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

import kotlin.internal.InlineOnly

// TODO(clr): needs ilemit Div_Un (see audit)
@PublishedApi
internal actual fun uintRemainder(v1: UInt, v2: UInt): UInt = TODO("clr binding should be implemented")

// TODO(clr): needs ilemit Div_Un (see audit)
@PublishedApi
internal actual fun uintDivide(v1: UInt, v2: UInt): UInt = TODO("clr binding should be implemented")

// TODO(clr): needs ilemit Div_Un (see audit)
@PublishedApi
internal actual fun ulongDivide(v1: ULong, v2: ULong): ULong = TODO("clr binding should be implemented")

// TODO(clr): needs ilemit Div_Un (see audit)
@PublishedApi
internal actual fun ulongRemainder(v1: ULong, v2: ULong): ULong = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun uintCompare(v1: Int, v2: Int): Int =
    (v1.toLong() and 0xFFFFFFFFL).compareTo(v2.toLong() and 0xFFFFFFFFL)

@PublishedApi
internal actual fun ulongCompare(v1: Long, v2: Long): Int =
    (v1 xor Long.MIN_VALUE).compareTo(v2 xor Long.MIN_VALUE)

@PublishedApi
@InlineOnly
internal actual inline fun uintToULong(value: Int): ULong = (value.toLong() and 0xFFFFFFFFL).toULong()

@PublishedApi
@InlineOnly
internal actual inline fun uintToLong(value: Int): Long = value.toLong() and 0xFFFFFFFFL

@PublishedApi
@InlineOnly
internal actual inline fun uintToFloat(value: Int): Float = (value.toLong() and 0xFFFFFFFFL).toFloat()

@PublishedApi
@InlineOnly
internal actual inline fun floatToUInt(value: Float): UInt = doubleToUInt(value.toDouble())

@PublishedApi
internal actual fun uintToDouble(value: Int): Double = (value.toLong() and 0xFFFFFFFFL).toDouble()

@PublishedApi
internal actual fun doubleToUInt(value: Double): UInt = when {
    value.isNaN() -> UInt.MIN_VALUE
    value <= UInt.MIN_VALUE.toDouble() -> UInt.MIN_VALUE
    value >= UInt.MAX_VALUE.toDouble() -> UInt.MAX_VALUE
    else -> value.toLong().toUInt()
}

@PublishedApi
@InlineOnly
internal actual inline fun ulongToFloat(value: Long): Float = ulongToDouble(value).toFloat()

@PublishedApi
@InlineOnly
internal actual inline fun floatToULong(value: Float): ULong = doubleToULong(value.toDouble())

@PublishedApi
internal actual fun ulongToDouble(value: Long): Double =
    (value ushr 11).toDouble() * 2048 + (value and 2047L)

@PublishedApi
internal actual fun doubleToULong(value: Double): ULong = when {
    value.isNaN() -> ULong.MIN_VALUE
    value <= ULong.MIN_VALUE.toDouble() -> ULong.MIN_VALUE
    value >= ULong.MAX_VALUE.toDouble() -> ULong.MAX_VALUE
    value < Long.MAX_VALUE.toDouble() -> value.toLong().toULong()
    else -> ULong((value - 9223372036854775808.0).toLong() or Long.MIN_VALUE)
}

@InlineOnly
internal actual inline fun uintToString(value: Int): String = value.toUInt().toString()

// TODO(clr): needs ilemit Div_Un (see audit)
@InlineOnly
internal actual inline fun uintToString(value: Int, base: Int): String = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun ulongToString(value: Long): String = value.toULong().toString()

// TODO(clr): needs ilemit Div_Un (see audit)
internal actual fun ulongToString(value: Long, base: Int): String = TODO("clr binding should be implemented")
