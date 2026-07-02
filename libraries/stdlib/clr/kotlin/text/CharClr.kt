/*
 * Copyright 2010-2021 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.text

// System.Char.GetUnicodeCategory(char) returns a System.Globalization.UnicodeCategory (an Int32-backed BCL enum);
// receiver maps to the static method's char argument (same shape as isLetter -> System.Char.IsLetter). The BCL enum
// reduces to its underlying Int32 ordinal, so the Kotlin-declared Int return carries the .NET ordinal (0..29).
@kotlin.clr.ClrIntrinsic("System.Char.GetUnicodeCategory")
private fun Char.nativeUnicodeCategory(): Int = TODO("@ClrIntrinsic System.Char.GetUnicodeCategory")

// Maps the .NET UnicodeCategory ordinal to Kotlin's CharCategory. The .NET ordinals differ from CharCategory.value
// (e.g. .NET OpenPunctuation=20 -> START_PUNCTUATION), so an explicit table is required.
public actual val Char.category: CharCategory
    get() = when (this.nativeUnicodeCategory()) {
        0 -> CharCategory.UPPERCASE_LETTER          // .NET UppercaseLetter
        1 -> CharCategory.LOWERCASE_LETTER          // .NET LowercaseLetter
        2 -> CharCategory.TITLECASE_LETTER          // .NET TitlecaseLetter
        3 -> CharCategory.MODIFIER_LETTER           // .NET ModifierLetter
        4 -> CharCategory.OTHER_LETTER              // .NET OtherLetter
        5 -> CharCategory.NON_SPACING_MARK          // .NET NonSpacingMark
        6 -> CharCategory.COMBINING_SPACING_MARK    // .NET SpacingCombiningMark
        7 -> CharCategory.ENCLOSING_MARK            // .NET EnclosingMark
        8 -> CharCategory.DECIMAL_DIGIT_NUMBER      // .NET DecimalDigitNumber
        9 -> CharCategory.LETTER_NUMBER             // .NET LetterNumber
        10 -> CharCategory.OTHER_NUMBER             // .NET OtherNumber
        11 -> CharCategory.SPACE_SEPARATOR          // .NET SpaceSeparator
        12 -> CharCategory.LINE_SEPARATOR           // .NET LineSeparator
        13 -> CharCategory.PARAGRAPH_SEPARATOR      // .NET ParagraphSeparator
        14 -> CharCategory.CONTROL                  // .NET Control
        15 -> CharCategory.FORMAT                   // .NET Format
        16 -> CharCategory.SURROGATE                // .NET Surrogate
        17 -> CharCategory.PRIVATE_USE              // .NET PrivateUse
        18 -> CharCategory.CONNECTOR_PUNCTUATION    // .NET ConnectorPunctuation
        19 -> CharCategory.DASH_PUNCTUATION         // .NET DashPunctuation
        20 -> CharCategory.START_PUNCTUATION        // .NET OpenPunctuation
        21 -> CharCategory.END_PUNCTUATION          // .NET ClosePunctuation
        22 -> CharCategory.INITIAL_QUOTE_PUNCTUATION// .NET InitialQuotePunctuation
        23 -> CharCategory.FINAL_QUOTE_PUNCTUATION  // .NET FinalQuotePunctuation
        24 -> CharCategory.OTHER_PUNCTUATION        // .NET OtherPunctuation
        25 -> CharCategory.MATH_SYMBOL              // .NET MathSymbol
        26 -> CharCategory.CURRENCY_SYMBOL          // .NET CurrencySymbol
        27 -> CharCategory.MODIFIER_SYMBOL          // .NET ModifierSymbol
        28 -> CharCategory.OTHER_SYMBOL             // .NET OtherSymbol
        else -> CharCategory.UNASSIGNED             // .NET OtherNotAssigned (29) and any unknown
    }

// category != CharCategory.UNASSIGNED (a char is "defined" iff Unicode assigns it a category).
@kotlin.internal.InlineOnly
public actual inline fun Char.isDefined(): Boolean = category != CharCategory.UNASSIGNED

@kotlin.clr.ClrIntrinsic("System.Char.IsLetter")
public actual fun Char.isLetter(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsLetterOrDigit")
public actual fun Char.isLetterOrDigit(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsDigit")
public actual fun Char.isDigit(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsControl")
public actual fun Char.isISOControl(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsWhiteSpace")
public actual fun Char.isWhitespace(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsUpper")
public actual fun Char.isUpperCase(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsLower")
public actual fun Char.isLowerCase(): Boolean = TODO("clr binding should be implemented")

@Deprecated("Use uppercaseChar() instead.", ReplaceWith("uppercaseChar()"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("System.Char.ToUpperInvariant")
public actual fun Char.toUpperCase(): Char = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToUpperInvariant")
public actual fun Char.uppercaseChar(): Char = TODO("clr binding should be implemented")

// Annotation-bug fix: System.Char.ToUpperInvariant returns Char, but this returns String.
// Route through String.uppercase() (BCL ToUpperInvariant). Note: loses the 'ß'->"SS" special-casing.
@SinceKotlin("1.5")
public actual fun Char.uppercase(): String = this.toString().uppercase()

@Deprecated("Use lowercaseChar() instead.", ReplaceWith("lowercaseChar()"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("System.Char.ToLowerInvariant")
public actual fun Char.toLowerCase(): Char = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToLowerInvariant")
public actual fun Char.lowercaseChar(): Char = TODO("clr binding should be implemented")

// Annotation-bug fix: System.Char.ToLowerInvariant returns Char, but this returns String.
// Route through String.lowercase() (BCL ToLowerInvariant).
@SinceKotlin("1.5")
public actual fun Char.lowercase(): String = this.toString().lowercase()

// category == CharCategory.TITLECASE_LETTER (.NET UnicodeCategory.TitlecaseLetter).
@kotlin.internal.InlineOnly
public actual inline fun Char.isTitleCase(): Boolean = category == CharCategory.TITLECASE_LETTER

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToUpperInvariant")
public actual fun Char.titlecaseChar(): Char = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsHighSurrogate")
public actual fun Char.isHighSurrogate(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsLowSurrogate")
public actual fun Char.isLowSurrogate(): Boolean = TODO("clr binding should be implemented")

// ASCII digit map (0-9, A-Z/a-z for radix > 10); -1 if not a valid digit in [radix].
// Self-contained: relies only on primitive Char comparison and `Char - Char` (mirrors clrDigitOf in StringNumberConversionsClr).
internal actual fun digitOf(char: Char, radix: Int): Int {
    val digit = when {
        char >= '0' && char <= '9' -> char - '0'
        char >= 'a' && char <= 'z' -> char - 'a' + 10
        char >= 'A' && char <= 'Z' -> char - 'A' + 10
        else -> -1
    }
    return if (digit >= 0 && digit < radix) digit else -1
}

@PublishedApi
internal actual fun checkRadix(radix: Int): Int {
    if (radix < 2 || radix > 36) {
        throw IllegalArgumentException("radix $radix was not in valid range 2..36")
    }
    return radix
}
