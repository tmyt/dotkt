/*
 * Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS", "TYPE_PARAMETER_AS_REIFIED")

// CLR array actuals share portable Kotlin policy with the pinned upstream sources.
// Native component allocation and overlap-safe copies remain platform bindings.

package kotlin.collections

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.elementAt(index: Int): T {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.elementAt(index: Int): Byte {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.elementAt(index: Int): Short {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun IntArray.elementAt(index: Int): Int {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun LongArray.elementAt(index: Int): Long {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.elementAt(index: Int): Float {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.elementAt(index: Int): Double {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.elementAt(index: Int): Boolean {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

@kotlin.internal.InlineOnly
public actual inline fun CharArray.elementAt(index: Int): Char {
    return elementAtOrElse(index) { throw IndexOutOfBoundsException("index: $index, size: $size}") }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun <T> Array<out T>.asList(): List<T> {
    return object : AbstractList<T>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: T): Boolean = this@asList.contains(element)
        override fun get(index: Int): T = this@asList[index]
        override fun indexOf(element: T): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: T): Int = this@asList.lastIndexOf(element)

        override fun iterator(): Iterator<T> = object : Iterator<T> {
            val size_ = size
            var index = 0
            override fun next(): T {
                if (index >= size_) throw NoSuchElementException()
                return this@asList[index++]
            }
            override fun hasNext(): Boolean = index < size_
        }
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun ByteArray.asList(): List<Byte> {
    return object : AbstractList<Byte>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Byte): Boolean = this@asList.contains(element)
        override fun get(index: Int): Byte = this@asList[index]
        override fun indexOf(element: Byte): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Byte): Int = this@asList.lastIndexOf(element)
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun ShortArray.asList(): List<Short> {
    return object : AbstractList<Short>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Short): Boolean = this@asList.contains(element)
        override fun get(index: Int): Short = this@asList[index]
        override fun indexOf(element: Short): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Short): Int = this@asList.lastIndexOf(element)
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun IntArray.asList(): List<Int> {
    return object : AbstractList<Int>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Int): Boolean = this@asList.contains(element)
        override fun get(index: Int): Int = this@asList[index]
        override fun indexOf(element: Int): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Int): Int = this@asList.lastIndexOf(element)
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun LongArray.asList(): List<Long> {
    return object : AbstractList<Long>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Long): Boolean = this@asList.contains(element)
        override fun get(index: Int): Long = this@asList[index]
        override fun indexOf(element: Long): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Long): Int = this@asList.lastIndexOf(element)
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun FloatArray.asList(): List<Float> {
    return object : AbstractList<Float>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Float): Boolean = this@asList.any { it.toBits() == element.toBits() }
        override fun get(index: Int): Float = this@asList[index]
        override fun indexOf(element: Float): Int = this@asList.indexOfFirst { it.toBits() == element.toBits() }
        override fun lastIndexOf(element: Float): Int = this@asList.indexOfLast { it.toBits() == element.toBits() }
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun DoubleArray.asList(): List<Double> {
    return object : AbstractList<Double>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Double): Boolean = this@asList.any { it.toBits() == element.toBits() }
        override fun get(index: Int): Double = this@asList[index]
        override fun indexOf(element: Double): Int = this@asList.indexOfFirst { it.toBits() == element.toBits() }
        override fun lastIndexOf(element: Double): Int = this@asList.indexOfLast { it.toBits() == element.toBits() }
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun BooleanArray.asList(): List<Boolean> {
    return object : AbstractList<Boolean>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Boolean): Boolean = this@asList.contains(element)
        override fun get(index: Int): Boolean = this@asList[index]
        override fun indexOf(element: Boolean): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Boolean): Int = this@asList.lastIndexOf(element)
    }
}

/**
 * Returns a [List] that wraps the original array.
 */
public actual fun CharArray.asList(): List<Char> {
    return object : AbstractList<Char>(), RandomAccess {
        override val size: Int get() = this@asList.size
        override fun isEmpty(): Boolean = this@asList.isEmpty()
        override fun contains(element: Char): Boolean = this@asList.contains(element)
        override fun get(index: Int): Char = this@asList[index]
        override fun indexOf(element: Char): Int = this@asList.indexOf(element)
        override fun lastIndexOf(element: Char): Int = this@asList.lastIndexOf(element)
    }
}

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.jvm.JvmName("contentDeepEqualsInline")
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>.contentDeepEquals(other: Array<out T>): Boolean {
    return this.contentDeepEquals(other)
}

@SinceKotlin("1.4")
@kotlin.jvm.JvmName("contentDeepEqualsNullable")
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>?.contentDeepEquals(other: Array<out T>?): Boolean {
    return contentDeepEqualsImpl(other)
}

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.jvm.JvmName("contentDeepHashCodeInline")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepHashCode(): Int {
    return this.contentDeepHashCode()
}

@SinceKotlin("1.4")
@kotlin.jvm.JvmName("contentDeepHashCodeNullable")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepHashCode(): Int {
    return contentDeepHashCodeImpl()
}

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.jvm.JvmName("contentDeepToStringInline")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepToString(): String {
    return this.contentDeepToString()
}

@SinceKotlin("1.4")
@kotlin.jvm.JvmName("contentDeepToStringNullable")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepToString(): String {
    return contentDeepToStringImpl()
}

@SinceKotlin("1.4")
public actual infix fun <T> Array<out T>?.contentEquals(other: Array<out T>?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun ByteArray?.contentEquals(other: ByteArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun ShortArray?.contentEquals(other: ShortArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun IntArray?.contentEquals(other: IntArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun LongArray?.contentEquals(other: LongArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun FloatArray?.contentEquals(other: FloatArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (!this[i].equals(other[i])) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun DoubleArray?.contentEquals(other: DoubleArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (!this[i].equals(other[i])) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun BooleanArray?.contentEquals(other: BooleanArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual infix fun CharArray?.contentEquals(other: CharArray?): Boolean {
    if (this === other) return true
    if (this === null || other === null) return false
    if (size != other.size) return false
    for (i in indices) {
        if (this[i] != other[i]) return false
    }
    return true
}

@SinceKotlin("1.4")
public actual fun <T> Array<out T>?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun ByteArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun ShortArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun IntArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun LongArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun FloatArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun DoubleArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun BooleanArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
public actual fun CharArray?.contentHashCode(): Int {
    if (this === null) return 0
    var result = 1
    for (element in this)
        result = 31 * result + element.hashCode()
    return result
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentToString(): String {
    if (this == null) return "null"
    val sb = StringBuilder("[")
    for (i in indices) {
        if (i > 0) sb.append(", ")
        sb.append(this[i])
    }
    sb.append("]")
    return sb.toString()
}

@SinceKotlin("1.4")
public actual fun ByteArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun ShortArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun IntArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun LongArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun FloatArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun DoubleArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun BooleanArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

@SinceKotlin("1.4")
public actual fun CharArray?.contentToString(): String {
    return this?.joinToString(", ", "[", "]") ?: "null"
}

// copyInto MUST be overlap-safe: ArrayDeque shifts in place via `elementData.copyInto(elementData, ...)`
// (self-copy, dest may be > or < src), so a naive forward element loop clobbers source slots before
// reading them (#97). System.Array.Copy is memmove (handles overlap in either direction) — the same
// @ClrIntrinsic("System.Array.Copy") helper copyOfRange already uses. One typed helper per array type
// so the receiver flows to Array.Copy's `System.Array sourceArray` with no cast (each primitive array
// type derives from System.Array).
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun ByteArray.nativeArrayCopy(sourceIndex: Int, dest: ByteArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun ShortArray.nativeArrayCopy(sourceIndex: Int, dest: ShortArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun IntArray.nativeArrayCopy(sourceIndex: Int, dest: IntArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun LongArray.nativeArrayCopy(sourceIndex: Int, dest: LongArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun FloatArray.nativeArrayCopy(sourceIndex: Int, dest: FloatArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun DoubleArray.nativeArrayCopy(sourceIndex: Int, dest: DoubleArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun BooleanArray.nativeArrayCopy(sourceIndex: Int, dest: BooleanArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")
@kotlin.clr.ClrIntrinsic("System.Array.Copy")
private fun CharArray.nativeArrayCopy(sourceIndex: Int, dest: CharArray, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.copyInto(destination: Array<T>, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): Array<T> {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    copyArrayRange(destination, startIndex, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.copyInto(destination: ByteArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ByteArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.copyInto(destination: ShortArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ShortArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.copyInto(destination: IntArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): IntArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.copyInto(destination: LongArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): LongArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.copyInto(destination: FloatArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): FloatArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.copyInto(destination: DoubleArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): DoubleArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.copyInto(destination: BooleanArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): BooleanArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

@SinceKotlin("1.3")
@IgnorableReturnValue
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.copyInto(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): CharArray {
    AbstractList.checkRangeIndexes(startIndex, endIndex, this.size)
    val rangeSize = endIndex - startIndex
    AbstractList.checkRangeIndexes(destinationOffset, destinationOffset + rangeSize, destination.size)
    nativeArrayCopy(startIndex, destination, destinationOffset, rangeSize)
    return destination
}

public actual fun <T> Array<T>.copyOf(): Array<T> {
    return this.copyOfUninitializedElements(size)
}

public actual fun ByteArray.copyOf(): ByteArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun ShortArray.copyOf(): ShortArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun IntArray.copyOf(): IntArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun LongArray.copyOf(): LongArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun FloatArray.copyOf(): FloatArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun DoubleArray.copyOf(): DoubleArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun BooleanArray.copyOf(): BooleanArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun CharArray.copyOf(): CharArray {
    return this.copyOfUninitializedElements(size)
}

public actual fun ByteArray.copyOf(newSize: Int): ByteArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun ShortArray.copyOf(newSize: Int): ShortArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun IntArray.copyOf(newSize: Int): IntArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun LongArray.copyOf(newSize: Int): LongArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun FloatArray.copyOf(newSize: Int): FloatArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun DoubleArray.copyOf(newSize: Int): DoubleArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun BooleanArray.copyOf(newSize: Int): BooleanArray {
    return this.copyOfUninitializedElements(newSize)
}

public actual fun CharArray.copyOf(newSize: Int): CharArray {
    return this.copyOfUninitializedElements(newSize)
}

// Native allocation preserves reference components; nullable value elements need object slots.
@kotlin.clr.ClrTypeAlias("System.Type")
private interface DotktType {
    @kotlin.clr.ClrIntrinsic("GetElementType") fun getElementType(): DotktType
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "IsValueType") fun isValueType(): Boolean
    @kotlin.clr.ClrIntrinsic("IsAssignableFrom") fun isAssignableFrom(other: DotktType): Boolean
}

@kotlin.clr.ClrIntrinsic("GetType")                            // object.GetType() -> the receiver's runtime array Type (e.g. int[])
private fun Any.dotktRuntimeType(): DotktType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Type.GetType")                // static Type.GetType(string) — resolves System.Object from CoreLib
private fun dotktTypeNamed(name: String): DotktType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")        // static Array.CreateInstance(Type, int) -> Array
private fun <T> dotktNewArrayOfType(elementType: DotktType, length: Int): Array<T?> = TODO("clr binding should be implemented")

public actual fun <T> Array<T>.copyOf(newSize: Int): Array<T?> {
    return this.copyOfNulls(newSize)
}

// Non-null generic copies retain the allocation's actual component, independently of T.
@kotlin.clr.ClrIntrinsic("GetType")                                     // object.GetType() -> the receiver's runtime array Type (e.g. int[])
private fun Any.nativeGetType(): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstanceFromArrayType")    // (Type arrayType, int length) -> Array; receiver(arrayType) -> arg0 (net10.0)
private fun Any.nativeCreateArrayLike(length: Int): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.Copy")                           // Copy(src, srcIndex, dst, dstIndex, length); receiver(src) -> arg0
private fun <T> Array<out T>.nativeArrayCopy(sourceIndex: Int, dest: Array<T>, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")

private fun <T> Array<out T>.copyArrayRange(destination: Array<T>, sourceIndex: Int, destinationIndex: Int, length: Int) {
    if (length == 0) return
    val sourceType = (this as Any).dotktRuntimeType()
    val destinationType = (destination as Any).dotktRuntimeType()
    var bulkCopy = destinationType.isAssignableFrom(sourceType)
    if (!bulkCopy) {
        val sourceElement = sourceType.getElementType()
        val destinationElement = destinationType.getElementType()
        bulkCopy = sourceElement.isValueType() && !destinationElement.isValueType() &&
            destinationElement.isAssignableFrom(sourceElement)
    }
    if (bulkCopy) {
        nativeArrayCopy(sourceIndex, destination, destinationIndex, length)
    } else {
        // Array.Copy also performs numeric widening. Ordinary Kotlin stores own
        // incompatible-component checks and must not acquire those conversions.
        for (index in 0 until length) destination[destinationIndex + index] = this[sourceIndex + index]
    }
}

internal fun checkCopyOfRangeArguments(fromIndex: Int, toIndex: Int, size: Int) {
    if (toIndex > size)
        throw IndexOutOfBoundsException("toIndex ($toIndex) is greater than size ($size).")
    if (fromIndex > toIndex)
        throw IllegalArgumentException("fromIndex ($fromIndex) is greater than toIndex ($toIndex).")
}

internal fun <T> Array<T>.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): Array<T> {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = (this as Any).nativeGetType().nativeCreateArrayLike(newSize) as Array<T>
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal actual fun <T> Array<T>.copyOfUninitializedElements(newSize: Int): Array<T> {
    return copyOfUninitializedElements(0, newSize)
}

internal fun <E> Array<E>.copyOfNulls(newSize: Int): Array<E?>  = copyOfNulls(0, newSize)

internal fun <E> Array<E>.copyOfNulls(fromIndex: Int, toIndex: Int): Array<E?> {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val element = (this as Any).dotktRuntimeType().getElementType()
    val nullableElement = if (element.isValueType()) dotktTypeNamed("System.Object") else element
    val result: Array<E?> = dotktNewArrayOfType(nullableElement, newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

public actual fun <T> Array<T>.copyOfRange(fromIndex: Int, toIndex: Int): Array<T> {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun ByteArray.copyOfRange(fromIndex: Int, toIndex: Int): ByteArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun ShortArray.copyOfRange(fromIndex: Int, toIndex: Int): ShortArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun IntArray.copyOfRange(fromIndex: Int, toIndex: Int): IntArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun LongArray.copyOfRange(fromIndex: Int, toIndex: Int): LongArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun FloatArray.copyOfRange(fromIndex: Int, toIndex: Int): FloatArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun DoubleArray.copyOfRange(fromIndex: Int, toIndex: Int): DoubleArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun BooleanArray.copyOfRange(fromIndex: Int, toIndex: Int): BooleanArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

public actual fun CharArray.copyOfRange(fromIndex: Int, toIndex: Int): CharArray {
    checkCopyOfRangeArguments(fromIndex, toIndex, size)
    return copyOfUninitializedElements(fromIndex, toIndex)
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<T>.fill(element: T, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.fill(element: Byte, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.fill(element: Short, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.fill(element: Int, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.fill(element: Long, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.fill(element: Float, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.fill(element: Double, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.fill(element: Boolean, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.fill(element: Char, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    for (index in fromIndex until toIndex) {
        this[index] = element
    }
}

public actual operator fun <T> Array<T>.plus(element: T): Array<T> {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun ByteArray.plus(element: Byte): ByteArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun ShortArray.plus(element: Short): ShortArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun IntArray.plus(element: Int): IntArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun LongArray.plus(element: Long): LongArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun FloatArray.plus(element: Float): FloatArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun DoubleArray.plus(element: Double): DoubleArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun BooleanArray.plus(element: Boolean): BooleanArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

public actual operator fun CharArray.plus(element: Char): CharArray {
    val index = size
    val result = copyOfUninitializedElements(index + 1)
    result[index] = element
    return result
}

@Suppress("UNCHECKED_CAST")
public actual operator fun <T> Array<T>.plus(elements: Collection<T>): Array<T> {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun ByteArray.plus(elements: Collection<Byte>): ByteArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun ShortArray.plus(elements: Collection<Short>): ShortArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun IntArray.plus(elements: Collection<Int>): IntArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun LongArray.plus(elements: Collection<Long>): LongArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun FloatArray.plus(elements: Collection<Float>): FloatArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun DoubleArray.plus(elements: Collection<Double>): DoubleArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun BooleanArray.plus(elements: Collection<Boolean>): BooleanArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

public actual operator fun CharArray.plus(elements: Collection<Char>): CharArray {
    var index = size
    val result = copyOfUninitializedElements(index + elements.size)
    for (element in elements) result[index++] = element
    return result
}

@Suppress("UNCHECKED_CAST")
public actual operator fun <T> Array<T>.plus(elements: Array<out T>): Array<T> {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun ByteArray.plus(elements: ByteArray): ByteArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun ShortArray.plus(elements: ShortArray): ShortArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun IntArray.plus(elements: IntArray): IntArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun LongArray.plus(elements: LongArray): LongArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun FloatArray.plus(elements: FloatArray): FloatArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun DoubleArray.plus(elements: DoubleArray): DoubleArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun BooleanArray.plus(elements: BooleanArray): BooleanArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

public actual operator fun CharArray.plus(elements: CharArray): CharArray {
    val thisSize = size
    val arraySize = elements.size
    val result = copyOfUninitializedElements(thisSize + arraySize)
    elements.copyInto(result, thisSize)
    return result
}

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.plusElement(element: T): Array<T> {
    return plus(element)
}

public actual fun IntArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun LongArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun ByteArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun ShortArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun DoubleArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun FloatArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun CharArray.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

public actual fun <T : Comparable<T>> Array<out T>.sort(): Unit {
    if (size > 1) sortArray(this, 0, size)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T : Comparable<T>> Array<out T>.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    if (fromIndex < toIndex - 1) sortArray(this, fromIndex, toIndex)
}

public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>): Unit {
    if (size > 1) sortArrayWith(this, 0, size, comparator)
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>, fromIndex: Int = 0, toIndex: Int = size): Unit {
    AbstractList.checkRangeIndexes(fromIndex, toIndex, size)
    sortArrayWith(this, fromIndex, toIndex, comparator)
}

public actual fun ByteArray.toTypedArray(): Array<Byte> {
    return Array(size) { index -> this[index] }
}

public actual fun ShortArray.toTypedArray(): Array<Short> {
    return Array(size) { index -> this[index] }
}

public actual fun IntArray.toTypedArray(): Array<Int> {
    return Array(size) { index -> this[index] }
}

public actual fun LongArray.toTypedArray(): Array<Long> {
    return Array(size) { index -> this[index] }
}

public actual fun FloatArray.toTypedArray(): Array<Float> {
    return Array(size) { index -> this[index] }
}

public actual fun DoubleArray.toTypedArray(): Array<Double> {
    return Array(size) { index -> this[index] }
}

public actual fun BooleanArray.toTypedArray(): Array<Boolean> {
    return Array(size) { index -> this[index] }
}

public actual fun CharArray.toTypedArray(): Array<Char> {
    return Array(size) { index -> this[index] }
}


internal fun ByteArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): ByteArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = ByteArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun ShortArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): ShortArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = ShortArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun IntArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): IntArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = IntArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun LongArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): LongArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = LongArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun FloatArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): FloatArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = FloatArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun DoubleArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): DoubleArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = DoubleArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun BooleanArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): BooleanArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = BooleanArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun CharArray.copyOfUninitializedElements(fromIndex: Int, toIndex: Int): CharArray {
    val newSize = toIndex - fromIndex
    if (newSize < 0) {
        throw IllegalArgumentException("$fromIndex > $toIndex")
    }
    val result = CharArray(newSize)
    this.copyInto(result, 0, fromIndex, toIndex.coerceAtMost(size))
    return result
}

internal fun ByteArray.copyOfUninitializedElements(newSize: Int): ByteArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun ShortArray.copyOfUninitializedElements(newSize: Int): ShortArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun IntArray.copyOfUninitializedElements(newSize: Int): IntArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun LongArray.copyOfUninitializedElements(newSize: Int): LongArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun FloatArray.copyOfUninitializedElements(newSize: Int): FloatArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun DoubleArray.copyOfUninitializedElements(newSize: Int): DoubleArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun BooleanArray.copyOfUninitializedElements(newSize: Int): BooleanArray {
    return copyOfUninitializedElements(0, newSize)
}

internal fun CharArray.copyOfUninitializedElements(newSize: Int): CharArray {
    return copyOfUninitializedElements(0, newSize)
}
