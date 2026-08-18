/*
 * Copyright 2010-2021 JetBrains s.r.o. and Kotlin Programming Language contributors.
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

@kotlin.clr.ClrIntrinsic("IndexOf")
internal actual fun String.nativeIndexOf(ch: Char, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeIndexOf(str: String, fromIndex: Int): Int = nativeIndexOfString(str, fromIndex)

@kotlin.clr.ClrIntrinsic("LastIndexOf")
internal actual fun String.nativeLastIndexOf(ch: Char, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeLastIndexOf(str: String, fromIndex: Int): Int = nativeLastIndexOfString(str, fromIndex)

// Ordinal substring search. Kotlin's `indexOf(String)` is case-sensitive ordinal, but .NET's
// `String.IndexOf(string)` is culture-sensitive; bound here as an explicit ordinal char scan instead.
// @PublishedApi (not private) so the public-ABI inline `nativeIndexOf` may reference it.
@PublishedApi
internal fun String.nativeIndexOfString(str: String, fromIndex: Int): Int {
    val thisLength = this.length
    val strLength = str.length
    val start = if (fromIndex < 0) 0 else fromIndex
    if (strLength == 0) return if (start > thisLength) thisLength else start
    val last = thisLength - strLength
    var i = start
    while (i <= last) {
        var j = 0
        while (j < strLength && this[i + j] == str[j]) j++
        if (j == strLength) return i
        i++
    }
    return -1
}

@PublishedApi
internal fun String.nativeLastIndexOfString(str: String, fromIndex: Int): Int {
    val thisLength = this.length
    val strLength = str.length
    if (strLength == 0) return if (fromIndex < 0) -1 else if (fromIndex > thisLength) thisLength else fromIndex
    var i = if (fromIndex > thisLength - strLength) thisLength - strLength else fromIndex
    while (i >= 0) {
        var j = 0
        while (j < strLength && this[i + j] == str[j]) j++
        if (j == strLength) return i
        i--
    }
    return -1
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String?.equals(other: String?, ignoreCase: Boolean = false): Boolean =
    if (this == null || other == null) this === other
    else if (!ignoreCase) this == other
    else this.uppercase() == other.uppercase()

// .NET String.Replace(char, char) is an ordinal replacement, matching Kotlin's case-sensitive replace.
@kotlin.clr.ClrIntrinsic("Replace")
private fun String.nativeReplace(oldChar: Char, newChar: Char): String = TODO("@Clr System.String.Replace(char,char)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replace(oldChar: Char, newChar: Char, ignoreCase: Boolean = false): String {
    if (!ignoreCase) return nativeReplace(oldChar, newChar)
    val source = this
    return buildString {
        for (i in 0 until source.length) {
            val c = source[i]
            append(if (c.equals(oldChar, ignoreCase = true)) newChar else c)
        }
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replace(oldValue: String, newValue: String, ignoreCase: Boolean = false): String {
    var occurrenceIndex: Int = indexOf(oldValue, 0, ignoreCase)
    // FAST PATH: no match
    if (occurrenceIndex < 0) return this

    val oldValueLength = oldValue.length
    val searchStep = oldValueLength.coerceAtLeast(1)
    val stringBuilder = StringBuilder()
    var i = 0
    // Append the end-exclusive String slice. Keeping the slice explicit here avoids introducing a second index/count
    // adaptation into this replacement loop; the selected append(String) wrapper owns nullable-string semantics.
    do {
        stringBuilder.append(this.substring(i, occurrenceIndex)).append(newValue)
        i = occurrenceIndex + oldValueLength
        if (occurrenceIndex >= length) break
        occurrenceIndex = indexOf(oldValue, occurrenceIndex + searchStep, ignoreCase)
    } while (occurrenceIndex > 0)

    return stringBuilder.append(this.substring(i, length)).toString()
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replaceFirst(oldChar: Char, newChar: Char, ignoreCase: Boolean = false): String {
    val index = indexOf(oldChar, ignoreCase = ignoreCase)
    return if (index < 0) this else substring(0, index) + newChar + substring(index + 1)
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replaceFirst(oldValue: String, newValue: String, ignoreCase: Boolean = false): String {
    val index = indexOf(oldValue, ignoreCase = ignoreCase)
    return if (index < 0) this else substring(0, index) + newValue + substring(index + oldValue.length)
}

@Deprecated("Use uppercase() instead.", ReplaceWith("uppercase(Locale.getDefault())", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("ToUpper")
public actual fun String.toUpperCase(): String = TODO("clr binding should be implemented")

// CLR-native 1:1 case mapping — see docs/dotkt-semantics.md §5g. .NET's ToUpperInvariant/ToLowerInvariant
// do simple 1:1 code-point mapping and deliberately do NOT perform the Unicode one-to-many full-mapping
// expansions ('ß' -> "SS", ligatures, ...) that Kotlin/JVM/Native/JS do. kotlin/clr takes the platform
// form: there is no binary interop with other Kotlin backends, so string-value parity is unneeded, and
// 1:1 mapping keeps Char/String case consistency + round-trip (the .NET platform choice).
@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("ToUpperInvariant")
public actual fun String.uppercase(): String = TODO("@Clr System.String.ToUpperInvariant")

@Deprecated("Use lowercase() instead.", ReplaceWith("lowercase(Locale.getDefault())", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("ToLower")
public actual fun String.toLowerCase(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("ToLowerInvariant")
public actual fun String.lowercase(): String = TODO("@Clr System.String.ToLowerInvariant")

@SinceKotlin("1.4")
public actual fun CharArray.concatToString(): String {
    val sb = StringBuilder()
    for (c in this) sb.append(c)
    return sb.toString()
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.concatToString(startIndex: Int = 0, endIndex: Int = this.size): String {
    val sb = StringBuilder()
    for (i in startIndex until endIndex) sb.append(this[i])
    return sb.toString()
}

// Thin wrapper: Kotlin toCharArray(startIndex, endIndex) has an exclusive end index, while
// .NET String.ToCharArray(startIndex, length) takes a length. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("ToCharArray")
private fun String.nativeToCharArray(startIndex: Int, length: Int): CharArray = TODO("@Clr System.String.ToCharArray(int,int)")

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.toCharArray(startIndex: Int = 0, endIndex: Int = this.length): CharArray =
    nativeToCharArray(startIndex, endIndex - startIndex)

// UTF-8 transcoding via a constructed `System.Text.UTF8Encoding` instance (the proven Regex/StringBuilder
// @ClrIntrinsic-class pattern). The class is `internal` (NOT private) so its constructor substitution is valid.
// The no-arg ctor is replacement-based (malformed -> U+FFFD); the (Boolean, Boolean) ctor binds
// UTF8Encoding(encoderShouldEmitBOM, throwOnInvalidBytes) whose throwing fallbacks raise
// DecoderFallbackException/EncoderFallbackException on malformed input — used for throwOnInvalidSequence=true.
@kotlin.clr.ClrTypeAlias("System.Text.UTF8Encoding")
internal class DotktUtf8 {
    constructor()
    constructor(encoderShouldEmitBOM: Boolean, throwOnInvalidBytes: Boolean)

    @kotlin.clr.ClrIntrinsic("GetString")
    fun getString(bytes: ByteArray): String = TODO("@Clr System.Text.UTF8Encoding.GetString(byte[])")

    @kotlin.clr.ClrIntrinsic("GetBytes")
    fun getBytes(s: String): ByteArray = TODO("@Clr System.Text.UTF8Encoding.GetBytes(string)")
}

@SinceKotlin("1.4")
public actual fun ByteArray.decodeToString(): String = DotktUtf8().getString(this)

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.decodeToString(
    startIndex: Int = 0,
    endIndex: Int = this.size,
    throwOnInvalidSequence: Boolean = false
): String {
    val slice = if (startIndex == 0 && endIndex == this.size) this else this.copyOfRange(startIndex, endIndex)
    // throwOnInvalidSequence=true: use a throwing UTF8Encoding and surface a CharacterCodingException
    // (per the Kotlin contract) on malformed UTF-8, instead of silently substituting U+FFFD.
    return if (throwOnInvalidSequence) {
        try {
            DotktUtf8(false, true).getString(slice)
        } catch (e: IllegalArgumentException) {
            // System.Text.Decoder/EncoderFallbackException both derive from System.ArgumentException
            // (= kotlin.IllegalArgumentException); a narrow catch lets an unrelated fault (OOM, a
            // miscompiled intrinsic path) propagate instead of being masked as CharacterCodingException.
            throw CharacterCodingException()
        }
    } else {
        DotktUtf8().getString(slice)
    }
}

@SinceKotlin("1.4")
public actual fun String.encodeToByteArray(): ByteArray = DotktUtf8().getBytes(this)

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.encodeToByteArray(
    startIndex: Int = 0,
    endIndex: Int = this.length,
    throwOnInvalidSequence: Boolean = false
): ByteArray {
    val slice = if (startIndex == 0 && endIndex == this.length) this else this.substring(startIndex, endIndex)
    // throwOnInvalidSequence=true: throw a CharacterCodingException (per the Kotlin contract) on an
    // unpaired surrogate, instead of encoding it as the U+FFFD replacement.
    return if (throwOnInvalidSequence) {
        try {
            DotktUtf8(false, true).getBytes(slice)
        } catch (e: IllegalArgumentException) {
            // System.Text.Decoder/EncoderFallbackException both derive from System.ArgumentException
            // (= kotlin.IllegalArgumentException); a narrow catch lets an unrelated fault (OOM, a
            // miscompiled intrinsic path) propagate instead of being masked as CharacterCodingException.
            throw CharacterCodingException()
        }
    } else {
        DotktUtf8().getBytes(slice)
    }
}

@kotlin.clr.ClrIntrinsic("ToCharArray")
public actual fun String.toCharArray(): CharArray = TODO("clr binding should be implemented")

// Thin wrapper: Kotlin toCharArray(startIndex, endIndex) has an exclusive end index, while
// .NET String.CopyTo(sourceIndex, destination, destinationIndex, count) takes a count. Adapt by subtracting.
// @PublishedApi (not private) so the public-ABI inline `toCharArray` may reference it across module boundaries.
@PublishedApi
@kotlin.clr.ClrIntrinsic("CopyTo")
internal fun String.nativeCopyTo(sourceIndex: Int, destination: CharArray, destinationIndex: Int, count: Int): Unit =
    TODO("@Clr System.String.CopyTo(int,char[],int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
@kotlin.internal.InlineOnly
public actual inline fun String.toCharArray(
    destination: CharArray,
    destinationOffset: Int = 0,
    startIndex: Int = 0,
    endIndex: Int = length
): CharArray {
    nativeCopyTo(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@kotlin.clr.ClrIntrinsic("Substring")
public actual fun String.substring(startIndex: Int): String = TODO("clr binding should be implemented")

// Thin wrapper: Kotlin substring(start, end) has an exclusive end index, while
// .NET String.Substring(startIndex, length) takes a length. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("Substring")
private fun String.nativeSubstring(startIndex: Int, length: Int): String = TODO("@Clr System.String.Substring(int,int)")

public actual fun String.substring(startIndex: Int, endIndex: Int): String =
    nativeSubstring(startIndex, endIndex - startIndex)

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.startsWith(prefix: String, ignoreCase: Boolean = false): Boolean {
    if (this.length < prefix.length) return false
    val region = this.substring(0, prefix.length)
    return if (!ignoreCase) region == prefix else region.uppercase() == prefix.uppercase()
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.startsWith(prefix: String, startIndex: Int, ignoreCase: Boolean = false): Boolean {
    if (startIndex < 0 || startIndex + prefix.length > this.length) return false
    val region = this.substring(startIndex, startIndex + prefix.length)
    return if (!ignoreCase) region == prefix else region.uppercase() == prefix.uppercase()
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.endsWith(suffix: String, ignoreCase: Boolean = false): Boolean {
    if (this.length < suffix.length) return false
    val region = this.substring(this.length - suffix.length)
    return if (!ignoreCase) region == suffix else region.uppercase() == suffix.uppercase()
}

@kotlin.internal.InlineOnly
public actual inline fun String(chars: CharArray): String = chars.concatToString()

@kotlin.internal.InlineOnly
public actual inline fun String(chars: CharArray, offset: Int, length: Int): String = chars.concatToString(offset, offset + length)

// Ordinal comparison: Kotlin's String.compareTo is ordinal, while .NET's default String.CompareTo/Compare
// is culture-sensitive; bound here to the explicitly-ordinal System.String.CompareOrdinal static method.
@kotlin.clr.ClrIntrinsic("System.String.CompareOrdinal")
private fun clrCompareOrdinal(strA: String, strB: String): Int = TODO("@Clr System.String.CompareOrdinal(string,string)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.compareTo(other: String, ignoreCase: Boolean = false): Int =
    if (!ignoreCase) clrCompareOrdinal(this, other)
    else clrCompareOrdinal(this.uppercase(), other.uppercase())

@SinceKotlin("1.5")
public actual infix fun CharSequence?.contentEquals(other: CharSequence?): Boolean = contentEqualsImpl(other)

@SinceKotlin("1.5")
public actual fun CharSequence?.contentEquals(other: CharSequence?, ignoreCase: Boolean): Boolean =
    if (ignoreCase) contentEqualsIgnoreCaseImpl(other) else contentEqualsImpl(other)

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharSequence.regionMatches(thisOffset: Int, other: CharSequence, otherOffset: Int, length: Int, ignoreCase: Boolean = false): Boolean =
    regionMatchesImpl(thisOffset, other, otherOffset, length, ignoreCase)

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.regionMatches(thisOffset: Int, other: String, otherOffset: Int, length: Int, ignoreCase: Boolean = false): Boolean =
    regionMatchesImpl(thisOffset, other, otherOffset, length, ignoreCase)

@Deprecated("Use replaceFirstChar instead.", ReplaceWith("replaceFirstChar { if (it.isLowerCase()) it.titlecase(Locale.getDefault()) else it.toString() }", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5")
public actual fun String.capitalize(): String =
    if (isNotEmpty()) substring(0, 1).uppercase() + substring(1) else this

@Deprecated("Use replaceFirstChar instead.", ReplaceWith("replaceFirstChar { it.lowercase(Locale.getDefault()) }", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5")
public actual fun String.decapitalize(): String =
    if (isNotEmpty()) substring(0, 1).lowercase() + substring(1) else this

public actual fun CharSequence.repeat(n: Int): String {
    require(n >= 0) { "Count 'n' must be non-negative, but was $n." }
    return buildString {
        repeat(n) { append(this@repeat) }
    }
}

public actual val String.Companion.CASE_INSENSITIVE_ORDER: Comparator<String>
    get() = Comparator { a, b -> a.compareTo(b, ignoreCase = true) }

// `String.format` — CLR PLATFORM API (like Kotlin/JVM's own `format`, which is JVM-only platform API; Native/JS
// have none). Bound to System.String.Format: the format string is the .NET COMPOSITE format ("{0} items",
// "{0:D5}", "{0,-4}"), NOT Java printf ("%d") — host conventions win. See docs/dotkt-semantics.md.
@kotlin.clr.ClrIntrinsic("System.String.Format")
private fun clrStringFormat(format: String, args: Array<out Any?>): String = TODO("@Clr System.String.Format(string, object[])")

/** Formats [args] into [format] using the .NET composite format (`"{0} items"`, `"{0:D5}"`). */
public fun String.Companion.format(format: String, vararg args: Any?): String = clrStringFormat(format, args)

/** Formats [args] into this string as a .NET composite format template (`"{0} items".format(5)`). */
public fun String.format(vararg args: Any?): String = clrStringFormat(this, args)
