/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
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

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.text

// --- Radix conversions ---
// `digitOf`/`checkRadix` in CharClr.kt are themselves still TODO, so these implementations are self-contained and rely
// only on primitive intrinsics (Char/Int/Long comparison, `Char + Int`, `Char - Char`) plus the BCL-bound StringBuilder
// (`insert`/`toString`) and the standard exception types. The `expect` declarations are non-inline, so `inline` is
// dropped here to allow the non-trivial bodies.

private fun clrCheckRadix(radix: Int): Int {
    if (radix < 2 || radix > 36) {
        throw IllegalArgumentException("radix $radix was not in valid range 2..36")
    }
    return radix
}

// Maps a digit value 0..35 to its character: 0..9 then a..z.
private fun clrDigitToChar(digit: Int): Char =
    if (digit < 10) '0' + digit else 'a' + (digit - 10)

// Returns the value of [char] as a digit in the given [radix], or -1 if it is not a valid digit in that radix.
private fun clrDigitOf(char: Char, radix: Int): Int {
    val digit = when {
        char >= '0' && char <= '9' -> char - '0'
        char >= 'a' && char <= 'z' -> char - 'a' + 10
        char >= 'A' && char <= 'Z' -> char - 'A' + 10
        else -> -1
    }
    return if (digit >= 0 && digit < radix) digit else -1
}

private fun clrNumberFormatError(input: String): Nothing =
    throw NumberFormatException("For input string: \"$input\"")

@SinceKotlin("1.1")
public actual fun Byte.toString(radix: Int): String = this.toInt().toString(radix)

@SinceKotlin("1.1")
public actual fun Short.toString(radix: Int): String = this.toInt().toString(radix)

@SinceKotlin("1.1")
public actual fun Int.toString(radix: Int): String {
    clrCheckRadix(radix)
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
    clrCheckRadix(radix)
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

// Integer parses delegate to the base-10 radix impl (below), NOT to System.<T>.Parse. Two reasons: (1) System.<T>.Parse
// is CULTURE-sensitive and lenient (accepts leading/trailing whitespace, group separators) — JVM's is strict base-10;
// (2) it throws System.FormatException, which a Kotlin `catch (e: NumberFormatException)` cannot catch. The radix impl
// throws the real Kotlin NumberFormatException (full IllegalArgumentException hierarchy), matching JVM exactly.
public actual fun String.toByte(): Byte = this.toByte(10)

@SinceKotlin("1.1")
public actual fun String.toByte(radix: Int): Byte {
    val v = this.toInt(radix)
    if (v < Byte.MIN_VALUE.toInt() || v > Byte.MAX_VALUE.toInt()) clrNumberFormatError(this)
    return v.toByte()
}

public actual fun String.toShort(): Short = this.toShort(10)

@SinceKotlin("1.1")
public actual fun String.toShort(radix: Int): Short {
    val v = this.toInt(radix)
    if (v < Short.MIN_VALUE.toInt() || v > Short.MAX_VALUE.toInt()) clrNumberFormatError(this)
    return v.toShort()
}

public actual fun String.toInt(): Int = this.toInt(10)

@SinceKotlin("1.1")
public actual fun String.toInt(radix: Int): Int {
    clrCheckRadix(radix)
    val length = this.length
    if (length == 0) clrNumberFormatError(this)

    val firstChar = this[0]
    val isNegative = firstChar == '-'
    val hasSign = isNegative || firstChar == '+'
    val start = if (hasSign) 1 else 0
    if (hasSign && length == 1) clrNumberFormatError(this)
    // Accumulate on the negative side so Int.MIN_VALUE is representable; flip sign at the end if positive.
    val limit = if (isNegative) Int.MIN_VALUE else -Int.MAX_VALUE

    val limitForMaxRadix = (-Int.MAX_VALUE) / 36
    var limitBeforeMul = limitForMaxRadix
    var result = 0
    for (i in start until length) {
        val digit = clrDigitOf(this[i], radix)
        if (digit < 0) clrNumberFormatError(this)
        if (result < limitBeforeMul) {
            if (limitBeforeMul == limitForMaxRadix) {
                limitBeforeMul = limit / radix
                if (result < limitBeforeMul) clrNumberFormatError(this)
            } else {
                clrNumberFormatError(this)
            }
        }
        result *= radix
        if (result < limit + digit) clrNumberFormatError(this)
        result -= digit
    }
    return if (isNegative) result else -result
}

public actual fun String.toLong(): Long = this.toLong(10)

@SinceKotlin("1.1")
public actual fun String.toLong(radix: Int): Long {
    clrCheckRadix(radix)
    val length = this.length
    if (length == 0) clrNumberFormatError(this)

    val firstChar = this[0]
    val isNegative = firstChar == '-'
    val hasSign = isNegative || firstChar == '+'
    val start = if (hasSign) 1 else 0
    if (hasSign && length == 1) clrNumberFormatError(this)
    val limit = if (isNegative) Long.MIN_VALUE else -Long.MAX_VALUE

    val limitForMaxRadix = (-Long.MAX_VALUE) / 36
    var limitBeforeMul = limitForMaxRadix
    var result = 0L
    for (i in start until length) {
        val digit = clrDigitOf(this[i], radix)
        if (digit < 0) clrNumberFormatError(this)
        if (result < limitBeforeMul) {
            if (limitBeforeMul == limitForMaxRadix) {
                limitBeforeMul = limit / radix
                if (result < limitBeforeMul) clrNumberFormatError(this)
            } else {
                clrNumberFormatError(this)
            }
        }
        result *= radix
        if (result < limit + digit) clrNumberFormatError(this)
        result -= digit
    }
    return if (isNegative) result else -result
}

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
