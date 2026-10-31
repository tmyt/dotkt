/*
 * Copyright 2010-2022 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin

public actual fun Double.isNaN(): Boolean = this != this

public actual fun Float.isNaN(): Boolean = this != this

public actual fun Double.isInfinite(): Boolean = (this == Double.POSITIVE_INFINITY) || (this == Double.NEGATIVE_INFINITY)

public actual fun Float.isInfinite(): Boolean = (this == Float.POSITIVE_INFINITY) || (this == Float.NEGATIVE_INFINITY)

public actual fun Double.isFinite(): Boolean = (toRawBits() and 0x7fffffff_ffffffffL) < 0x7ff00000_00000000L

public actual fun Float.isFinite(): Boolean = (toRawBits() and 0x7fffffff) < 0x7f800000

@SinceKotlin("1.2")
public actual fun Double.toBits(): Long = if (this.isNaN()) 0x7ff8000000000000L else this.toRawBits()

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.DoubleToInt64Bits")
public actual fun Double.toRawBits(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Double.Companion.fromBits(bits: Long): Double = clrDoubleFromBits(bits)

@kotlin.clr.ClrIntrinsic("System.BitConverter.Int64BitsToDouble")
private fun clrDoubleFromBits(bits: Long): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Float.toBits(): Int = if (this.isNaN()) 0x7fc00000 else this.toRawBits()

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.SingleToInt32Bits")
public actual fun Float.toRawBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Float.Companion.fromBits(bits: Int): Float = clrFloatFromBits(bits)

@kotlin.clr.ClrIntrinsic("System.BitConverter.Int32BitsToSingle")
private fun clrFloatFromBits(bits: Int): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.PopCount")
public actual fun Int.countOneBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.LeadingZeroCount")
public actual fun Int.countLeadingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.TrailingZeroCount")
public actual fun Int.countTrailingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
public actual fun Int.takeHighestOneBit(): Int =
    if (this == 0) 0 else 1.shl(32 - 1 - countLeadingZeroBits())

@SinceKotlin("1.4")
public actual fun Int.takeLowestOneBit(): Int =
    this and -this

@SinceKotlin("1.6")
public actual fun Int.rotateLeft(bitCount: Int): Int =
    shl(bitCount) or ushr(32 - bitCount)

@SinceKotlin("1.6")
public actual fun Int.rotateRight(bitCount: Int): Int =
    shl(32 - bitCount) or ushr(bitCount)

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.PopCount")
public actual fun Long.countOneBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.LeadingZeroCount")
public actual fun Long.countLeadingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.TrailingZeroCount")
public actual fun Long.countTrailingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
public actual fun Long.takeHighestOneBit(): Long =
    if (this == 0L) 0L else 1L.shl(64 - 1 - countLeadingZeroBits())

@SinceKotlin("1.4")
public actual fun Long.takeLowestOneBit(): Long =
    this and -this

@SinceKotlin("1.6")
public actual fun Long.rotateLeft(bitCount: Int): Long =
    shl(bitCount) or ushr(64 - bitCount)

@SinceKotlin("1.6")
@kotlin.internal.InlineOnly
public actual inline fun Long.rotateRight(bitCount: Int): Long =
    shl(64 - bitCount) or ushr(bitCount)

// ---- Double/Float total-order equality & comparison (C14) ----
// Kotlin contracts a TOTAL order on Double/Float that differs from IEEE (and from System.Double.Equals /
// System.Double.CompareTo): `-0.0` sorts strictly below `0.0`, and `NaN` is the LARGEST value with `NaN == NaN`
// (a single canonical NaN). The primitive `==`/`<`/`>` operators keep the fast IEEE semantics; these helpers are
// what a BOXED `==` (`kotlin.Any.equals` on a boxed floating value) and `Comparable.compareTo` route to, so
// structural equality and ordered comparison follow Kotlin, not the CLR default. The bit compare uses `toBits()`
// (NaN-canonicalizing) — for `-0.0` the sign bit makes its Long/Int pattern negative, hence below `0.0`.

/** Kotlin total-order equality of two Double values: `NaN == NaN`, `-0.0 != 0.0`. */
public fun clrDoubleEquals(a: Double, b: Double): Boolean = a.toBits() == b.toBits()

/** Kotlin total-order equality of two Float values: `NaN == NaN`, `-0.0f != 0.0f`. */
public fun clrFloatEquals(a: Float, b: Float): Boolean = a.toBits() == b.toBits()

/** Kotlin total-order comparison of two Double values (`-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN) == 0`). */
public fun clrDoubleCompare(a: Double, b: Double): Int {
    if (a < b) return -1
    if (a > b) return 1
    val ab = a.toBits()
    val bb = b.toBits()
    return if (ab == bb) 0 else if (ab < bb) -1 else 1
}

/** Kotlin total-order comparison of two Float values (`-0.0f < 0.0f`, NaN largest, `NaN.compareTo(NaN) == 0`). */
public fun clrFloatCompare(a: Float, b: Float): Int {
    if (a < b) return -1
    if (a > b) return 1
    val ab = a.toBits()
    val bb = b.toBits()
    return if (ab == bb) 0 else if (ab < bb) -1 else 1
}
