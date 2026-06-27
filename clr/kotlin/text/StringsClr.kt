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

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeIndexOf(ch: Char, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeIndexOf(str: String, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeLastIndexOf(ch: Char, fromIndex: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
internal actual inline fun String.nativeLastIndexOf(str: String, fromIndex: Int): Int = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String?.equals(other: String?, ignoreCase: Boolean = false): Boolean { TODO("clr binding should be implemented") }

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
@kotlin.internal.InlineOnly
public actual inline fun String.toUpperCase(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.internal.InlineOnly
public actual inline fun String.uppercase(): String = TODO("clr binding should be implemented")

@Deprecated("Use lowercase() instead.", ReplaceWith("lowercase(Locale.getDefault())", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.internal.InlineOnly
public actual inline fun String.toLowerCase(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.internal.InlineOnly
public actual inline fun String.lowercase(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
public actual fun CharArray.concatToString(): String { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.concatToString(startIndex: Int = 0, endIndex: Int = this.size): String { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.toCharArray(startIndex: Int = 0, endIndex: Int = this.length): CharArray { TODO("clr binding should be implemented") }

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

@kotlin.internal.InlineOnly
public actual inline fun String.toCharArray(): CharArray = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
@kotlin.internal.InlineOnly
public actual inline fun String.toCharArray(
    destination: CharArray,
    destinationOffset: Int = 0,
    startIndex: Int = 0,
    endIndex: Int = length
): CharArray = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.substring(startIndex: Int): String = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.substring(startIndex: Int, endIndex: Int): String = TODO("clr binding should be implemented")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.startsWith(prefix: String, ignoreCase: Boolean = false): Boolean { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.startsWith(prefix: String, startIndex: Int, ignoreCase: Boolean = false): Boolean { TODO("clr binding should be implemented") }

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun String.endsWith(suffix: String, ignoreCase: Boolean = false): Boolean { TODO("clr binding should be implemented") }

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
public actual fun String.capitalize(): String { TODO("clr binding should be implemented") }

@Deprecated("Use replaceFirstChar instead.", ReplaceWith("replaceFirstChar { it.lowercase(Locale.getDefault()) }", "java.util.Locale"))
@DeprecatedSinceKotlin(warningSince = "1.5")
public actual fun String.decapitalize(): String { TODO("clr binding should be implemented") }

public actual fun CharSequence.repeat(n: Int): String { TODO("clr binding should be implemented") }

public actual val String.Companion.CASE_INSENSITIVE_ORDER: Comparator<String>
    get() = TODO("clr binding should be implemented")
