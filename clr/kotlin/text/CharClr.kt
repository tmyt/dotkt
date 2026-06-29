/*
 * Copyright 2010-2021 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.text

public actual val Char.category: CharCategory
    get() = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun Char.isDefined(): Boolean = TODO("clr binding should be implemented")

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

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToUpperInvariant")
public actual fun Char.uppercase(): String = TODO("clr binding should be implemented")

@Deprecated("Use lowercaseChar() instead.", ReplaceWith("lowercaseChar()"))
@DeprecatedSinceKotlin(warningSince = "1.5", errorSince = "2.1")
@kotlin.clr.ClrIntrinsic("System.Char.ToLowerInvariant")
public actual fun Char.toLowerCase(): Char = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToLowerInvariant")
public actual fun Char.lowercaseChar(): Char = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToLowerInvariant")
public actual fun Char.lowercase(): String = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun Char.isTitleCase(): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
@kotlin.clr.ClrIntrinsic("System.Char.ToUpperInvariant")
public actual fun Char.titlecaseChar(): Char = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsHighSurrogate")
public actual fun Char.isHighSurrogate(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Char.IsLowSurrogate")
public actual fun Char.isLowSurrogate(): Boolean = TODO("clr binding should be implemented")

internal actual fun digitOf(char: Char, radix: Int): Int = TODO("clr binding should be implemented")

@PublishedApi
internal actual fun checkRadix(radix: Int): Int { TODO("clr binding should be implemented") }
