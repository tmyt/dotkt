/*
 * Copyright 2010-2025 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "PLATFORM_CLASS_MAPPED_TO_KOTLIN",
)

package kotlin.text

// Maps a digit value 0..35 to its character: 0..9 then a..z.
private fun clrDigitToChar(digit: Int): Char =
    if (digit < 10) '0' + digit else 'a' + (digit - 10)

private fun clrNumberFormatError(input: String): Nothing =
    throw NumberFormatException("For input string: \"$input\"")

@SinceKotlin("1.1")
public actual fun Byte.toString(radix: Int): String = this.toInt().toString(radix)

@SinceKotlin("1.1")
public actual fun Short.toString(radix: Int): String = this.toInt().toString(radix)

@SinceKotlin("1.1")
public actual fun Int.toString(radix: Int): String {
    checkRadix(radix)
    if (this == 0) return "0"
    val negative = this < 0
    var n = this
    val sb = StringBuilder()
    // Accumulate least-significant digit first using negative-side arithmetic so that Int.MIN_VALUE
    // (which has no positive counterpart) is handled without overflow; digits are prepended.
    do {
        val rem = n % radix
        sb.insert(0, clrDigitToChar(if (rem < 0) -rem else rem))
        n /= radix
    } while (n != 0)
    if (negative) sb.insert(0, '-')
    return sb.toString()
}

@SinceKotlin("1.1")
public actual fun Long.toString(radix: Int): String {
    checkRadix(radix)
    if (this == 0L) return "0"
    val negative = this < 0
    var n = this
    val sb = StringBuilder()
    do {
        val rem = (n % radix).toInt()
        sb.insert(0, clrDigitToChar(if (rem < 0) -rem else rem))
        n /= radix
    } while (n != 0L)
    if (negative) sb.insert(0, '-')
    return sb.toString()
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun String?.toBoolean(): Boolean = this != null && this.equals("true", ignoreCase = true)

/**
 * Parses the string to a [Byte] number.
 *
 * The string must consist of an optional leading `+` or `-` sign and decimal digits (`0-9`),
 * and fit the valid [Byte] value range (within `Byte.MIN_VALUE..Byte.MAX_VALUE`),
 * otherwise a [NumberFormatException] will be thrown.
 *
 * @throws NumberFormatException if the string is not a valid representation of a [Byte].
 * @sample samples.text.Numbers.toByte
 */
public actual fun String.toByte(): Byte = toByteOrNull() ?: numberFormatError(this)

/**
 * Parses the string as a signed [Byte] number and returns the result.
 * @throws NumberFormatException if the string is not a valid representation of a number.
 * @throws IllegalArgumentException when [radix] is not a valid radix for string to number conversion.
 */
@SinceKotlin("1.1")
public actual fun String.toByte(radix: Int): Byte = toByteOrNull(radix) ?: numberFormatError(this)

/**
 * Parses the string to a [Short] number.
 *
 * The string must consist of an optional leading `+` or `-` sign and decimal digits (`0-9`),
 * and fit the valid [Short] value range (within `Short.MIN_VALUE..Short.MAX_VALUE`),
 * otherwise a [NumberFormatException] will be thrown.
 *
 * @throws NumberFormatException if the string is not a valid representation of a [Short].
 * @sample samples.text.Numbers.toShort
 */
public actual fun String.toShort(): Short = toShortOrNull() ?: numberFormatError(this)

/**
 * Parses the string as a [Short] number and returns the result.
 * @throws NumberFormatException if the string is not a valid representation of a number.
 * @throws IllegalArgumentException when [radix] is not a valid radix for string to number conversion.
 */
@SinceKotlin("1.1")
public actual fun String.toShort(radix: Int): Short = toShortOrNull(radix) ?: numberFormatError(this)

/**
 * Parses the string to an [Int] number.
 *
 * The string must consist of an optional leading `+` or `-` sign and decimal digits (`0-9`),
 * and fit the valid [Int] value range (within `Int.MIN_VALUE..Int.MAX_VALUE`),
 * otherwise a [NumberFormatException] will be thrown.
 *
 * @throws NumberFormatException if the string is not a valid representation of an [Int].
 * @sample samples.text.Numbers.toInt
 */
public actual fun String.toInt(): Int = toIntOrNull() ?: numberFormatError(this)

/**
 * Parses the string as an [Int] number and returns the result.
 * @throws NumberFormatException if the string is not a valid representation of a number.
 * @throws IllegalArgumentException when [radix] is not a valid radix for string to number conversion.
 */
@SinceKotlin("1.1")
public actual fun String.toInt(radix: Int): Int = toIntOrNull(radix) ?: numberFormatError(this)

/**
 * Parses the string to a [Long] number.
 *
 * The string must consist of an optional leading `+` or `-` sign and decimal digits (`0-9`),
 * and fit the valid [Long] value range (within `Long.MIN_VALUE..Long.MAX_VALUE`),
 * otherwise a [NumberFormatException] will be thrown.
 *
 * @throws NumberFormatException if the string is not a valid representation of a [Long].
 * @sample samples.text.Numbers.toLong
 */
public actual fun String.toLong(): Long = toLongOrNull() ?: numberFormatError(this)

/**
 * Parses the string as a [Long] number and returns the result.
 * @throws NumberFormatException if the string is not a valid representation of a number.
 * @throws IllegalArgumentException when [radix] is not a valid radix for string to number conversion.
 */
@SinceKotlin("1.1")
public actual fun String.toLong(radix: Int): Long = toLongOrNull(radix) ?: numberFormatError(this)

// System.<T>.Parse(string) uses the CURRENT culture, so e.g. "3,14".toDouble() silently succeeds in a comma-decimal
// locale instead of throwing (JVM parses '.' only). Bind the InvariantCulture-taking overloads and route through them,
// then convert the .NET FormatException into Kotlin's NumberFormatException so `catch (e: NumberFormatException)` works.
@kotlin.clr.ClrTypeAlias("System.IFormatProvider")
private interface ClrFormatProvider

@kotlin.clr.ClrIntrinsic("System.Globalization.CultureInfo.get_InvariantCulture")
private fun clrInvariantCulture(): ClrFormatProvider = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Single.Parse")
private fun clrParseFloat(s: String, provider: ClrFormatProvider): Float = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Double.Parse")
private fun clrParseDouble(s: String, provider: ClrFormatProvider): Double = TODO("clr binding should be implemented")

// System.<T>.Parse(string, provider) implicitly enables NumberStyles.AllowThousands, so an InvariantCulture parse of
// "3,14" would read the comma as a GROUP separator (-> 314.0) instead of throwing. JVM never accepts a group separator,
// and InvariantCulture's group separator is exactly ',', so reject any ',' up front — this pins the accepted grammar to
// JVM's (leading/trailing sign, decimal point, exponent) and makes "3,14".toDouble() throw as required.
private fun clrRejectGroupSeparator(s: String) {
    var i = 0
    while (i < s.length) { if (s[i] == ',') clrNumberFormatError(s); i++ }
}

public actual fun String.toFloat(): Float {
    clrRejectGroupSeparator(this)
    return try { clrParseFloat(this, clrInvariantCulture()) } catch (e: Throwable) { clrNumberFormatError(this) }
}

public actual fun String.toDouble(): Double {
    clrRejectGroupSeparator(this)
    return try { clrParseDouble(this, clrInvariantCulture()) } catch (e: Throwable) { clrNumberFormatError(this) }
}

@SinceKotlin("1.1")
public actual fun String.toFloatOrNull(): Float? = try { this.toFloat() } catch (e: Throwable) { null }

@SinceKotlin("1.1")
public actual fun String.toDoubleOrNull(): Double? = try { this.toDouble() } catch (e: Throwable) { null }
