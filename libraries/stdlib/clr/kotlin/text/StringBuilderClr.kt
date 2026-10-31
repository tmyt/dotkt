/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.text

@kotlin.clr.ClrTypeAlias("System.Text.StringBuilder")
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
    actual override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = substring(startIndex, endIndex)
    @kotlin.clr.ClrIntrinsic("Append")
    actual override fun append(value: Char): StringBuilder = TODO("clr binding should be implemented")
    actual override fun append(value: CharSequence?): StringBuilder = append((value ?: "null").toString())
    actual override fun append(value: CharSequence?, startIndex: Int, endIndex: Int): StringBuilder =
        this.appendRange(value ?: "null", startIndex, endIndex)

    public actual fun reverse(): StringBuilder {
        // Native/Wasm's surrogate-preserving algorithm over the CLR character indexer.
        if (this.length < 2) {
            return this
        }
        var end = length - 1
        var front = 0
        var frontLeadingChar = this[0]
        var endTrailingChar = this[end]
        var allowFrontSurrogate = true
        var allowEndSurrogate = true
        while (front < length / 2) {
            var frontTrailingChar = this[front + 1]
            var endLeadingChar = this[end - 1]
            var surrogateAtFront = allowFrontSurrogate && frontTrailingChar.isLowSurrogate() && frontLeadingChar.isHighSurrogate()
            if (surrogateAtFront && length < 3) {
                return this
            }
            var surrogateAtEnd = allowEndSurrogate && endTrailingChar.isLowSurrogate() && endLeadingChar.isHighSurrogate()
            allowFrontSurrogate = true
            allowEndSurrogate = true
            when {
                surrogateAtFront && surrogateAtEnd -> {
                    this[end] = frontTrailingChar
                    this[end - 1] = frontLeadingChar
                    this[front] = endLeadingChar
                    this[front + 1] = endTrailingChar
                    frontLeadingChar = this[front + 2]
                    endTrailingChar = this[end - 2]
                    front++
                    end--
                }
                !surrogateAtFront && !surrogateAtEnd -> {
                    this[end] = frontLeadingChar
                    this[front] = endTrailingChar
                    frontLeadingChar = frontTrailingChar
                    endTrailingChar = endLeadingChar
                }
                surrogateAtFront && !surrogateAtEnd -> {
                    this[end] = frontTrailingChar
                    this[front] = endTrailingChar
                    endTrailingChar = endLeadingChar
                    allowFrontSurrogate = false
                }
                !surrogateAtFront && surrogateAtEnd -> {
                    this[end] = frontLeadingChar
                    this[front] = endLeadingChar
                    frontLeadingChar = frontTrailingChar
                    allowEndSurrogate = false
                }
            }
            front++
            end--
        }
        if (length % 2 == 1 && (!allowEndSurrogate || !allowFrontSurrogate)) {
            this[end] = if (allowFrontSurrogate) endTrailingChar else frontLeadingChar
        }
        return this
    }
    public actual fun append(value: Any?): StringBuilder = append(value.toString())

    @SinceKotlin("1.3")
    public actual fun append(value: Boolean): StringBuilder = append(value.toString())

    @SinceKotlin("1.9")
    public actual fun append(value: Int): StringBuilder = append(value.toString())

    @SinceKotlin("1.9")
    public actual fun append(value: Long): StringBuilder = append(value.toString())

    @SinceKotlin("1.9")
    public actual fun append(value: Float): StringBuilder = append(value.toString())

    @SinceKotlin("1.9")
    public actual fun append(value: Double): StringBuilder = append(value.toString())

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Append")
    public actual fun append(value: CharArray): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.3")
    public actual fun append(value: String?): StringBuilder = nativeAppendString(value ?: "null")

    @kotlin.clr.ClrIntrinsic("Append")
    private fun nativeAppendString(value: String): StringBuilder = TODO("@Clr System.Text.StringBuilder.Append(string)")

    @SinceKotlin("1.3")
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Capacity")
    public actual fun capacity(): Int = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("EnsureCapacity")
    public actual fun ensureCapacity(minimumCapacity: Int): Unit {
        TODO("clr binding should be implemented")
    }

    @SinceKotlin("1.4")
    public actual fun indexOf(string: String): Int = toString().indexOf(string)

    @SinceKotlin("1.4")
    public actual fun indexOf(string: String, startIndex: Int): Int = toString().indexOf(string, startIndex)

    @SinceKotlin("1.4")
    public actual fun lastIndexOf(string: String): Int = toString().lastIndexOf(string)

    @SinceKotlin("1.4")
    public actual fun lastIndexOf(string: String, startIndex: Int): Int = toString().lastIndexOf(string, startIndex)

    @SinceKotlin("1.4")
    public actual fun insert(index: Int, value: Boolean): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.9")
    public actual fun insert(index: Int, value: Int): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.9")
    public actual fun insert(index: Int, value: Long): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.9")
    public actual fun insert(index: Int, value: Float): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.9")
    public actual fun insert(index: Int, value: Double): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: Char): StringBuilder = TODO("clr binding should be implemented")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrIntrinsic("Insert")
    public actual fun insert(index: Int, value: CharArray): StringBuilder = TODO("clr binding should be implemented")

    // No 1:1 BCL overload (.NET StringBuilder has no Insert(int, CharSequence)); insert the string form,
    // mirroring append(CharSequence?) above. Delegates to the @ClrIntrinsic Insert(int, String?) overload.
    @SinceKotlin("1.4")
    public actual fun insert(index: Int, value: CharSequence?): StringBuilder =
        insert(index, (value ?: "null").toString())

    @SinceKotlin("1.4")
    public actual fun insert(index: Int, value: Any?): StringBuilder = insert(index, value.toString())

    @SinceKotlin("1.4")
    public actual fun insert(index: Int, value: String?): StringBuilder = nativeInsertString(index, value ?: "null")

    @kotlin.clr.ClrIntrinsic("Insert")
    private fun nativeInsertString(index: Int, value: String): StringBuilder =
        TODO("@Clr System.Text.StringBuilder.Insert(int,string)")

    @SinceKotlin("1.4")
    @kotlin.clr.ClrProperty(kotlin.clr.WRITE, "Length")
    public actual fun setLength(newLength: Int): Unit {
        TODO("clr binding should be implemented")
    }

    @SinceKotlin("1.4")
    public actual fun substring(startIndex: Int): String {
        return substring(startIndex, length)
    }

    @SinceKotlin("1.4")
    public actual fun substring(startIndex: Int, endIndex: Int): String {
        AbstractList.checkBoundsIndexes(startIndex, endIndex, length)
        return nativeSubstring(startIndex, endIndex - startIndex)
    }

    // Thin wrapper: Kotlin substring/subSequence take an exclusive end index, while
    // .NET StringBuilder.ToString(startIndex, length) takes a length. Adapt by subtracting.
    @kotlin.clr.ClrIntrinsic("ToString")
    private fun nativeSubstring(startIndex: Int, length: Int): String = TODO("@Clr System.Text.StringBuilder.ToString(int,int)")

    // Setter of .NET StringBuilder.Capacity; used to shrink the backing buffer to the current length.
    @kotlin.clr.ClrProperty(kotlin.clr.WRITE, "Capacity")
    private fun nativeSetCapacity(value: Int): Unit = TODO("@Clr System.Text.StringBuilder.set_Capacity(int)")

    @SinceKotlin("1.4")
    public actual fun trimToSize(): Unit {
        nativeSetCapacity(length)
    }
}

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.append(value: Byte): StringBuilder = this.append(value.toInt())

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.append(value: Short): StringBuilder = this.append(value.toInt())

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.insert(index: Int, value: Byte): StringBuilder = this.insert(index, value.toInt())

@SinceKotlin("1.9")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.insert(index: Int, value: Short): StringBuilder = this.insert(index, value.toInt())

@SinceKotlin("1.3")
public actual fun StringBuilder.clear(): StringBuilder = apply { setLength(0) }

@kotlin.internal.InlineOnly
@kotlin.clr.ClrIntrinsic("set_Chars")
public actual operator fun StringBuilder.set(index: Int, value: Char): Unit = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.setRange(startIndex: Int, endIndex: Int, value: String): StringBuilder =
    this.deleteRange(startIndex, endIndex).insert(startIndex, value)

// Thin wrapper: Kotlin deleteAt removes a single index and deleteRange takes an exclusive
// end index, while .NET StringBuilder.Remove(startIndex, length) takes a length. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("Remove")
private fun StringBuilder.nativeRemove(startIndex: Int, count: Int): StringBuilder = TODO("@Clr System.Text.StringBuilder.Remove(int,int)")

@SinceKotlin("1.4")
public actual fun StringBuilder.deleteAt(index: Int): StringBuilder {
    AbstractList.checkElementIndex(index, length)
    return nativeRemove(index, 1)
}

@SinceKotlin("1.4")
public actual fun StringBuilder.deleteRange(startIndex: Int, endIndex: Int): StringBuilder {
    checkReplaceRange(startIndex, endIndex, length)
    val coercedEndIndex = endIndex.coerceAtMost(length)
    return nativeRemove(startIndex, coercedEndIndex - startIndex)
}

private fun checkReplaceRange(startIndex: Int, endIndex: Int, length: Int) {
    if (startIndex < 0 || startIndex > length) {
        throw IndexOutOfBoundsException("startIndex: $startIndex, length: $length")
    }
    if (startIndex > endIndex) {
        throw IllegalArgumentException("startIndex($startIndex) > endIndex($endIndex)")
    }
}

// Thin wrapper: Kotlin toCharArray(startIndex, endIndex) has an exclusive end index, while
// .NET StringBuilder.CopyTo(sourceIndex, destination, destinationIndex, count) takes a count. Adapt by subtracting.
// Kotlin bounds policy is applied by `toCharArray` before this physical copy.
@PublishedApi
@kotlin.clr.ClrIntrinsic("CopyTo")
internal fun StringBuilder.nativeCopyTo(sourceIndex: Int, destination: CharArray, destinationIndex: Int, count: Int): Unit =
    TODO("@Clr System.Text.StringBuilder.CopyTo(int,char[],int,int)")

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun StringBuilder.toCharArray(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = this.length): Unit {
    AbstractList.checkBoundsIndexes(startIndex, endIndex, length)
    AbstractList.checkBoundsIndexes(destinationOffset, destinationOffset + endIndex - startIndex, destination.size)
    nativeCopyTo(startIndex, destination, destinationOffset, endIndex - startIndex)
}

// Thin wrapper: Kotlin appendRange takes an exclusive end index, while
// .NET StringBuilder.Append(value, startIndex, charCount) takes a count. Adapt by subtracting.
@PublishedApi
@kotlin.clr.ClrIntrinsic("Append")
internal fun StringBuilder.nativeAppendRange(value: CharArray, startIndex: Int, charCount: Int): StringBuilder =
    TODO("@Clr System.Text.StringBuilder.Append(char[],int,int)")

@SinceKotlin("1.4")
public actual fun StringBuilder.appendRange(value: CharArray, startIndex: Int, endIndex: Int): StringBuilder {
    AbstractList.checkBoundsIndexes(startIndex, endIndex, value.size)
    return nativeAppendRange(value, startIndex, endIndex - startIndex)
}

@SinceKotlin("1.4")
public actual fun StringBuilder.appendRange(value: CharSequence, startIndex: Int, endIndex: Int): StringBuilder {
    AbstractList.checkBoundsIndexes(startIndex, endIndex, value.length)
    return append(value.subSequence(startIndex, endIndex).toString())
}

// Thin wrapper: Kotlin insertRange takes an exclusive end index, while
// .NET StringBuilder.Insert(index, value, startIndex, charCount) takes a count. Adapt by subtracting.
@PublishedApi
@kotlin.clr.ClrIntrinsic("Insert")
internal fun StringBuilder.nativeInsertRange(index: Int, value: CharArray, startIndex: Int, charCount: Int): StringBuilder =
    TODO("@Clr System.Text.StringBuilder.Insert(int,char[],int,int)")

@SinceKotlin("1.4")
public actual fun StringBuilder.insertRange(index: Int, value: CharArray, startIndex: Int, endIndex: Int): StringBuilder {
    AbstractList.checkPositionIndex(index, length)
    AbstractList.checkBoundsIndexes(startIndex, endIndex, value.size)
    return nativeInsertRange(index, value, startIndex, endIndex - startIndex)
}

@SinceKotlin("1.4")
public actual fun StringBuilder.insertRange(index: Int, value: CharSequence, startIndex: Int, endIndex: Int): StringBuilder {
    AbstractList.checkPositionIndex(index, length)
    AbstractList.checkBoundsIndexes(startIndex, endIndex, value.length)
    return insert(index, value.subSequence(startIndex, endIndex).toString())
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Int): StringBuilder = append(value).appendLine()

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Short): StringBuilder = append(value).appendLine()

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Byte): StringBuilder = append(value).appendLine()

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Long): StringBuilder = append(value).appendLine()

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Float): StringBuilder = append(value).appendLine()

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun StringBuilder.appendLine(value: Double): StringBuilder = append(value).appendLine()
