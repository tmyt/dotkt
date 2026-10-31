/*
 * Copyright 2010-2022 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.math

import kotlin.internal.InlineOnly

// region ================ Double Math ========================================

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Sin")
public actual fun sin(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Cos")
public actual fun cos(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Tan")
public actual fun tan(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Asin")
public actual fun asin(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Acos")
public actual fun acos(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Atan")
public actual fun atan(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Atan2")
public actual fun atan2(y: Double, x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Sinh")
public actual fun sinh(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Cosh")
public actual fun cosh(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Tanh")
public actual fun tanh(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.Math.Asinh")
public actual fun asinh(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.Math.Acosh")
public actual fun acosh(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.Math.Atanh")
public actual fun atanh(x: Double): Double = TODO("clr binding should be implemented")

// Binds System.Double.Hypot (net10): numerically correct for large magnitudes where sqrt(x*x+y*y) overflows.
@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Double.Hypot")
public actual fun hypot(x: Double, y: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Sqrt")
public actual fun sqrt(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Exp")
public actual fun exp(x: Double): Double = TODO("clr binding should be implemented")

// Binds System.Double.ExpM1 (net10): retains precision near 0 where exp(x)-1 loses it to cancellation.
@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Double.ExpM1")
public actual fun expm1(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun log(x: Double, base: Double): Double {
    if (base <= 0.0 || base == 1.0) return Double.NaN
    return ln(x) / ln(base)
}

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Log")
public actual fun ln(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Log10")
public actual fun log10(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.Math.Log2")
public actual fun log2(x: Double): Double = TODO("clr binding should be implemented")

// Binds System.Double.LogP1 (net10): retains precision near 0 where ln(1+x) loses it to cancellation.
@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Double.LogP1")
public actual fun ln1p(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Ceiling")
public actual fun ceil(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Floor")
public actual fun floor(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun truncate(x: Double): Double = when {
    x.isNaN() || x.isInfinite() -> x
    x > 0 -> floor(x)
    else -> ceil(x)
}

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Round")
public actual fun round(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Abs")
public actual fun abs(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun sign(x: Double): Double = when {
    x.isNaN() -> Double.NaN
    x > 0 -> 1.0
    x < 0 -> -1.0
    else -> x
}

@SinceKotlin("1.2")
public actual fun min(a: Double, b: Double): Double = when {
    a.isNaN() || b.isNaN() -> Double.NaN
    a == 0.0 && b == 0.0 -> if (a.signBit()) a else b   // -0.0 < +0.0
    else -> if (a < b) a else b
}

@SinceKotlin("1.2")
public actual fun max(a: Double, b: Double): Double = when {
    a.isNaN() || b.isNaN() -> Double.NaN
    a == 0.0 && b == 0.0 -> if (!a.signBit()) a else b  // -0.0 < +0.0
    else -> if (a > b) a else b
}

@SinceKotlin("1.8")
@WasExperimental(ExperimentalStdlibApi::class)
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Cbrt")
public actual fun cbrt(x: Double): Double = TODO("clr binding should be implemented")

// extensions

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.Pow")
public actual fun Double.pow(x: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Double.pow(n: Int): Double = pow(n.toDouble())

@SinceKotlin("1.2")
public actual val Double.absoluteValue: Double get() = abs(this)

@SinceKotlin("1.2")
public actual val Double.sign: Double get() = sign(this)

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.CopySign")
public actual fun Double.withSign(sign: Double): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Double.withSign(sign: Int): Double = withSign(sign.toDouble())

@SinceKotlin("1.2")
public actual val Double.ulp: Double
    get() = when {
        isNaN() -> Double.NaN
        isInfinite() -> Double.POSITIVE_INFINITY
        this == Double.MAX_VALUE || this == -Double.MAX_VALUE -> 2.0.pow(971)
        else -> {
            val d = absoluteValue
            d.nextUp() - d
        }
    }

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.BitIncrement")
public actual fun Double.nextUp(): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Math.BitDecrement")
public actual fun Double.nextDown(): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
public actual inline fun Double.nextTowards(to: Double): Double = when {
    this.isNaN() || to.isNaN() -> Double.NaN
    this == to -> to
    this < to -> this.nextUp()
    else -> this.nextDown()
}

// roundToInt/roundToLong are round-half-UP toward +inf (0.5->1, 2.5->3, -2.5->-2, -0.5->0) via floor(x+0.5),
// NOT banker's rounding — kotlin.math.round IS ties-to-even (System.Math.Round) and stays as-is, but these
// wrappers must NOT delegate to it (#103). NaN throws; out-of-range saturates to Int/Long MIN/MAX per contract.
@SinceKotlin("1.2")
public actual fun Double.roundToInt(): Int = when {
    isNaN() -> throw IllegalArgumentException("Cannot round NaN value.")
    this > Int.MAX_VALUE -> Int.MAX_VALUE
    this < Int.MIN_VALUE -> Int.MIN_VALUE
    else -> floor(this + 0.5).toInt()
}

@SinceKotlin("1.2")
public actual fun Double.roundToLong(): Long = when {
    isNaN() -> throw IllegalArgumentException("Cannot round NaN value.")
    this > Long.MAX_VALUE -> Long.MAX_VALUE
    this < Long.MIN_VALUE -> Long.MIN_VALUE
    else -> floor(this + 0.5).toLong()
}

// endregion

// region ================ Float Math ========================================

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Sin")
public actual fun sin(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Cos")
public actual fun cos(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Tan")
public actual fun tan(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Asin")
public actual fun asin(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Acos")
public actual fun acos(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Atan")
public actual fun atan(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Atan2")
public actual fun atan2(y: Float, x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Sinh")
public actual fun sinh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Cosh")
public actual fun cosh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Tanh")
public actual fun tanh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Asinh")
public actual fun asinh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Acosh")
public actual fun acosh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Atanh")
public actual fun atanh(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Single.Hypot")
public actual fun hypot(x: Float, y: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Sqrt")
public actual fun sqrt(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Exp")
public actual fun exp(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Single.ExpM1")
public actual fun expm1(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun log(x: Float, base: Float): Float {
    if (base <= 0.0F || base == 1.0F) return Float.NaN
    return ln(x) / ln(base)
}

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Log")
public actual fun ln(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Log10")
public actual fun log10(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.MathF.Log2")
public actual fun log2(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.Single.LogP1")
public actual fun ln1p(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Ceiling")
public actual fun ceil(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Floor")
public actual fun floor(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun truncate(x: Float): Float = when {
    x.isNaN() || x.isInfinite() -> x
    x > 0 -> floor(x)
    else -> ceil(x)
}

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Round")
public actual fun round(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Abs")
public actual fun abs(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun sign(x: Float): Float = when {
    x.isNaN() -> Float.NaN
    x > 0 -> 1.0f
    x < 0 -> -1.0f
    else -> x
}

@SinceKotlin("1.2")
public actual fun min(a: Float, b: Float): Float = when {
    a.isNaN() || b.isNaN() -> Float.NaN
    a == 0.0f && b == 0.0f -> if (a.signBit()) a else b   // -0.0 < +0.0
    else -> if (a < b) a else b
}

@SinceKotlin("1.2")
public actual fun max(a: Float, b: Float): Float = when {
    a.isNaN() || b.isNaN() -> Float.NaN
    a == 0.0f && b == 0.0f -> if (!a.signBit()) a else b   // -0.0 < +0.0
    else -> if (a > b) a else b
}

@SinceKotlin("1.8")
@WasExperimental(ExperimentalStdlibApi::class)
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Cbrt")
public actual fun cbrt(x: Float): Float = TODO("clr binding should be implemented")

// extensions

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.Pow")
public actual fun Float.pow(x: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Float.pow(n: Int): Float = pow(n.toFloat())

@SinceKotlin("1.2")
public actual val Float.absoluteValue: Float get() = abs(this)

@SinceKotlin("1.2")
public actual val Float.sign: Float get() = sign(this)

@SinceKotlin("1.2")
@InlineOnly
@kotlin.clr.ClrIntrinsic("System.MathF.CopySign")
public actual fun Float.withSign(sign: Float): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Float.withSign(sign: Int): Float = withSign(sign.toFloat())

// Float rounds through Double (#103): Float->Double is lossless, so Double.roundToInt (floor(x+0.5)) is EXACT for
// every float input and matches Kotlin/JVM (which defines Float.roundToLong = toDouble().roundToLong()), while
// float-precision floor(x+0.5f) would round odd integral floats in [2^23, 2^24) off by one. NaN throws; clamps inside.
@SinceKotlin("1.2")
public actual fun Float.roundToInt(): Int = this.toDouble().roundToInt()

@SinceKotlin("1.2")
public actual fun Float.roundToLong(): Long = this.toDouble().roundToLong()

// endregion

// region ================ Integer Math ========================================

@SinceKotlin("1.2")
@InlineOnly
// Pure-Kotlin body (NOT @ClrIntrinsic System.Math.Abs): Kotlin's abs WRAPS at MIN_VALUE
// (unchecked negation) — abs(Int.MIN_VALUE) == Int.MIN_VALUE — whereas System.Math.Abs's
// checked overload throws OverflowException at MIN. The unary minus below emits a plain
// `neg` IL op (unchecked), matching Kotlin semantics.
public actual inline fun abs(n: Int): Int = if (n < 0) -n else n

@SinceKotlin("1.2")
public actual fun min(a: Int, b: Int): Int = if (a < b) a else b

@SinceKotlin("1.2")
public actual fun max(a: Int, b: Int): Int = if (a > b) a else b

@SinceKotlin("1.2")
public actual val Int.absoluteValue: Int get() = abs(this)

@SinceKotlin("1.2")
public actual val Int.sign: Int
    get() = when {
        this < 0 -> -1
        this > 0 -> 1
        else -> 0
    }

@SinceKotlin("1.2")
@InlineOnly
// Pure-Kotlin body (NOT @ClrIntrinsic System.Math.Abs): Kotlin's abs WRAPS at MIN_VALUE
// (unchecked negation) — abs(Long.MIN_VALUE) == Long.MIN_VALUE — whereas System.Math.Abs's
// checked overload throws OverflowException at MIN. The unary minus below emits a plain
// `neg` IL op (unchecked), matching Kotlin semantics.
public actual inline fun abs(n: Long): Long = if (n < 0) -n else n

@SinceKotlin("1.2")
public actual fun min(a: Long, b: Long): Long = if (a < b) a else b

@SinceKotlin("1.2")
public actual fun max(a: Long, b: Long): Long = if (a > b) a else b

@SinceKotlin("1.2")
public actual val Long.absoluteValue: Long get() = abs(this)

@SinceKotlin("1.2")
public actual val Long.sign: Int
    get() = when {
        this < 0 -> -1
        this > 0 -> 1
        else -> 0
    }

@kotlin.clr.ClrIntrinsic("System.Double.IsNegative")
private fun Double.signBit(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Single.IsNegative")
private fun Float.signBit(): Boolean = TODO("clr binding should be implemented")

// endregion
