/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.text

@kotlin.clr.ClrIntrinsic("System.Text.StringBuilder")
public actual class StringBuilder : Appendable, CharSequence {
    public actual constructor()
    public actual constructor(capacity: Int)
    public actual constructor(content: CharSequence)
    @SinceKotlin("1.3")
    public actual constructor(content: String)

    @kotlin.clr.ClrIntrinsic("Length")
    actual override val length: Int
        get() = TODO("clr binding should be implemented")

    @kotlin.clr.ClrIntrinsic("get_Chars")
    actual override operator fun get(index: Int): Char = TODO("clr binding should be implemented")
    actual override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = nativeSubstring(startIndex, endIndex - startIndex)
    @kotlin.clr.ClrIntrinsic("Append")
    actual override fun append(value: Char): StringBuilder = TODO("clr binding should be implemented")
    actual override fun append(value: CharSequence?): StringBuilder = TODO("clr binding should be implemented")
    actual override fun append(value: CharSequence?, startIndex: Int, endIndex: Int): StringBuilder = TODO("clr binding should be implemented")

    public actual fun reverse(): StringBuilder = TODO("clr binding should be implemented")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Any?): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.3")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Boolean): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Int): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Long): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Float): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: Double): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: CharArray): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.3")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: String?): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.3")
    @kotlin.clr.ClrIntrinsic("get_Capacity")
    public actual fun capacity(): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("EnsureCapacity")
    public actual fun ensureCapacity(minimumCapacity: Int): Unit {
        TODO("clr binding should be implemented")
    }

    @SinceKotlin("1.4")
    public actual fun indexOf(string: String): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    public actual fun indexOf(string: String, startIndex: Int): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    public actual fun lastIndexOf(string: String): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    public actual fun lastIndexOf(string: String, startIndex: Int): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Boolean): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Int): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Long): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Float): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.9")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Double): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Char): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: CharArray): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    public actual fun insert(index: Int, value: CharSequence?): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Any?): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: String?): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("set_Length")
    public actual fun setLength(newLength: Int): Unit {
        TODO("clr binding should be implemented")
    }

    @SinceKotlin("1.4")
    public actual fun substring(startIndex: Int): String = nativeSubstring(startIndex, length - startIndex)

    @SinceKotlin("1.4")
    public actual fun substring(startIndex: Int, endIndex: Int): String = nativeSubstring(startIndex, endIndex - startIndex)

    // Thin wrapper: Kotlin substring/subSequence take an exclusive end index, while
    // .NET StringBuilder.ToString(startIndex, length) takes a length. Adapt by subtracting.
    @kotlin.clr.ClrIntrinsic("ToString")
    private fun nativeSubstring(startIndex: Int, length: Int): String = TODO("@Clr System.Text.StringBuilder.ToString(int,int)")

    @SinceKotlin("1.4")
    public actual fun trimToSize(): Unit {
        TODO("clr binding should be implemented")
    }
}

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("Append")
public actual fun StringBuilder.append(value: Byte): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("Append")
public actual fun StringBuilder.append(value: Short): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("Insert")
public actual fun StringBuilder.insert(index: Int, value: Byte): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("Insert")
public actual fun StringBuilder.insert(index: Int, value: Short): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@kotlin.clr.ClrIntrinsic("Clear")
public actual fun StringBuilder.clear(): StringBuilder = TODO("clr binding should be implemented")

@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("set_Chars")
public actual operator fun StringBuilder.set(index: Int, value: Char): Unit = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.setRange(startIndex: Int, endIndex: Int, value: String): StringBuilder = TODO("clr binding should be implemented")

// Thin wrapper: Kotlin deleteAt removes a single index and deleteRange takes an exclusive
// end index, while .NET StringBuilder.Remove(startIndex, length) takes a length. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("Remove")
private fun StringBuilder.nativeRemove(startIndex: Int, count: Int): StringBuilder = TODO("@Clr System.Text.StringBuilder.Remove(int,int)")

@SinceKotlin("1.4")
public actual fun StringBuilder.deleteAt(index: Int): StringBuilder = nativeRemove(index, 1)

@SinceKotlin("1.4")
public actual fun StringBuilder.deleteRange(startIndex: Int, endIndex: Int): StringBuilder = nativeRemove(startIndex, endIndex - startIndex)

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual inline fun StringBuilder.toCharArray(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = this.length): Unit = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendRange(value: CharArray, startIndex: Int, endIndex: Int): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendRange(value: CharSequence, startIndex: Int, endIndex: Int): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.insertRange(index: Int, value: CharArray, startIndex: Int, endIndex: Int): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.insertRange(index: Int, value: CharSequence, startIndex: Int, endIndex: Int): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Int): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Short): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Byte): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Long): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Float): StringBuilder = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Double): StringBuilder = TODO("clr binding should be implemented")
