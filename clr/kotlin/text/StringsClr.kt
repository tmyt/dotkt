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
internal actual inline fun String.nativeIndexOf(str: String, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("LastIndexOf")
internal actual fun String.nativeLastIndexOf(ch: Char, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeLastIndexOf(str: String, fromIndex: Int): Int = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String?.equals(other: String?, ignoreCase: Boolean = false): Boolean =
    if (this == null || other == null) this === other
    else if (!ignoreCase) this == other
    else this.uppercase() == other.uppercase()

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replace(oldChar: Char, newChar: Char, ignoreCase: Boolean = false): String { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replace(oldValue: String, newValue: String, ignoreCase: Boolean = false): String { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replaceFirst(oldChar: Char, newChar: Char, ignoreCase: Boolean = false): String { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.replaceFirst(oldValue: String, newValue: String, ignoreCase: Boolean = false): String { TODO("clr binding should be implemented") }

@Deprecated("Use uppercase() instead.", ReplaceWith("uppercase(Locale.getDefault())", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("ToUpper")
public actual fun String.toUpperCase(): String = TODO("clr binding should be implemented")

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

// UTF-8 transcoding -> TODO. The natural binding is `System.Text.UTF8Encoding().GetString/GetBytes`, but a private @Clr
// helper class (unlike the public `actual` StringBuilder) emits an InvalidProgram at the ctor-substitution; needs the
// @Clr-class-constructor substitution fixed for non-`actual` helper classes.
@SinceKotlin("1.4")
public actual fun ByteArray.decodeToString(): String { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.decodeToString(
    startIndex: Int = 0,
    endIndex: Int = this.size,
    throwOnInvalidSequence: Boolean = false
): String { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
public actual fun String.encodeToByteArray(): ByteArray { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.encodeToByteArray(
    startIndex: Int = 0,
    endIndex: Int = this.length,
    throwOnInvalidSequence: Boolean = false
): ByteArray { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("ToCharArray")
public actual fun String.toCharArray(): CharArray = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
@kotlin.internal.InlineOnly
public actual inline fun String.toCharArray(
    destination: CharArray,
    destinationOffset: Int = 0,
    startIndex: Int = 0,
    endIndex: Int = length
): CharArray = TODO("clr binding should be implemented")

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
public actual inline fun String(chars: CharArray): String = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String(chars: CharArray, offset: Int, length: Int): String = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.compareTo(other: String, ignoreCase: Boolean = false): Int { TODO("clr binding should be implemented") }

@SinceKotlin("1.5")
public actual infix fun CharSequence?.contentEquals(other: CharSequence?): Boolean { TODO("clr binding should be implemented") }

@SinceKotlin("1.5")
public actual fun CharSequence?.contentEquals(other: CharSequence?, ignoreCase: Boolean): Boolean { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharSequence.regionMatches(thisOffset: Int, other: CharSequence, otherOffset: Int, length: Int, ignoreCase: Boolean = false): Boolean { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.regionMatches(thisOffset: Int, other: String, otherOffset: Int, length: Int, ignoreCase: Boolean = false): Boolean = TODO("clr binding should be implemented")

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
    get() = TODO("clr binding should be implemented")
