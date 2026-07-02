@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

import kotlin.internal.InlineOnly

// Division/remainder are pure-Kotlin ports of the JVM actual (Guava's UnsignedLongs algorithm). They only run when
// a call goes through the emitted method (e.g. `toString(radix)` below); a direct `a / b` on UInt/ULong is lowered
// by the frontend to a raw `bin /` whose unsigned CLR operand type selects the native `div.un`/`rem.un` in ilemit.
@PublishedApi
internal actual fun uintRemainder(v1: UInt, v2: UInt): UInt = (v1.toLong() % v2.toLong()).toUInt()

@PublishedApi
internal actual fun uintDivide(v1: UInt, v2: UInt): UInt = (v1.toLong() / v2.toLong()).toUInt()

@PublishedApi
internal actual fun ulongDivide(v1: ULong, v2: ULong): ULong {
    val dividend = v1.toLong()
    val divisor = v2.toLong()
    if (divisor < 0) { // i.e., divisor >= 2^63:
        return if (v1 < v2) ULong(0) else ULong(1)
    }

    // Optimization - use signed division if both dividend and divisor < 2^63
    if (dividend >= 0) {
        return ULong(dividend / divisor)
    }

    // Otherwise, approximate the quotient, check, and correct if necessary.
    val quotient = ((dividend ushr 1) / divisor) shl 1
    val rem = dividend - quotient * divisor
    return ULong(quotient + if (ULong(rem) >= ULong(divisor)) 1 else 0)
}

@PublishedApi
internal actual fun ulongRemainder(v1: ULong, v2: ULong): ULong {
    val dividend = v1.toLong()
    val divisor = v2.toLong()
    if (divisor < 0) { // i.e., divisor >= 2^63:
        return if (v1 < v2) {
            v1 // dividend < divisor
        } else {
            v1 - v2 // dividend >= divisor
        }
    }

    // Optimization - use signed modulus if both dividend and divisor < 2^63
    if (dividend >= 0) {
        return ULong(dividend % divisor)
    }

    // Otherwise, approximate the quotient, check, and correct if necessary.
    val quotient = ((dividend ushr 1) / divisor) shl 1
    val rem = dividend - quotient * divisor
    return ULong(rem - if (ULong(rem) >= ULong(divisor)) divisor else 0)
}

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

@InlineOnly
internal actual inline fun uintToString(value: Int, base: Int): String = ulongToString(value.toLong() and 0xFFFFFFFFL, base)

@InlineOnly
internal actual inline fun ulongToString(value: Long): String = value.toULong().toString()

// Self-contained digit loop over the unsigned bit pattern via ulongDivide/ulongRemainder. Deliberately does NOT
// delegate to `Long.toString(radix)` (the JVM actual's shape): that call site is still lowered to
// System.Convert.ToString, which only supports bases 2/8/10/16 (see BirEmitter's toString(radix) lowering — kept
// until the stdlib digit-loop body is fixed, master-task-inventory bundle 1). `base` is pre-checked by checkRadix
// in the public UInt/ULong.toString(radix) wrappers.
internal actual fun ulongToString(value: Long, base: Int): String {
    if (value == 0L) return "0"
    val digits = "0123456789abcdefghijklmnopqrstuvwxyz"
    val b = ULong(base.toLong())
    var n = value
    val sb = StringBuilder()
    while (n != 0L) {
        sb.append(digits[ulongRemainder(ULong(n), b).toLong().toInt()])
        n = ulongDivide(ULong(n), b).toLong()
    }
    sb.reverse()
    return sb.toString()
}
