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

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun Byte.toString(radix: Int): String = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun Short.toString(radix: Int): String = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun Int.toString(radix: Int): String = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun Long.toString(radix: Int): String = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun String?.toBoolean(): Boolean = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toByte(): Byte = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun String.toByte(radix: Int): Byte = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toShort(): Short = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun String.toShort(radix: Int): Short = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toInt(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun String.toInt(radix: Int): Int = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toLong(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.InlineOnly
public actual inline fun String.toLong(radix: Int): Long = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toFloat(): Float = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
public actual inline fun String.toDouble(): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
public actual fun String.toFloatOrNull(): Float? = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
public actual fun String.toDoubleOrNull(): Double? = TODO("clr binding should be implemented")
