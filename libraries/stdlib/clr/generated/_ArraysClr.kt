/*
 * Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS", "TYPE_PARAMETER_AS_REIFIED")

// Step-1 CLR stub mirroring the JVM `actual` declarations of _ArraysJvm.kt.
// Bodies are `TODO` pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
// copyOf / copyOf(newSize) / copyOfRange / plus(element) / plusElement / contentEquals / contentHashCode /
// contentToString / toTypedArray carry real Kotlin bodies: the array constructor (`<Type>Array(size) { ... }`)
// and indexed get are compiler intrinsics, so no @Clr/BCL binding is needed for them.

package kotlin.collections

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.elementAt(index: Int): T = this[index]

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.elementAt(index: Int): Byte = this[index]

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.elementAt(index: Int): Short = this[index]

@kotlin.internal.InlineOnly
public actual inline fun IntArray.elementAt(index: Int): Int = this[index]

@kotlin.internal.InlineOnly
public actual inline fun LongArray.elementAt(index: Int): Long = this[index]

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.elementAt(index: Int): Float = this[index]

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.elementAt(index: Int): Double = this[index]

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.elementAt(index: Int): Boolean = this[index]

@kotlin.internal.InlineOnly
public actual inline fun CharArray.elementAt(index: Int): Char = this[index]

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
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>?.contentEquals(other: Array<out T>?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun ByteArray?.contentEquals(other: ByteArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun ShortArray?.contentEquals(other: ShortArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun IntArray?.contentEquals(other: IntArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun LongArray?.contentEquals(other: LongArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun FloatArray?.contentEquals(other: FloatArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (!this[i].equals(other[i])) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun DoubleArray?.contentEquals(other: DoubleArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (!this[i].equals(other[i])) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun BooleanArray?.contentEquals(other: BooleanArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun CharArray?.contentEquals(other: CharArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + (e?.hashCode() ?: 0)
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ByteArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun ShortArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun IntArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun LongArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun FloatArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun DoubleArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun BooleanArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun CharArray?.contentHashCode(): Int {
    if (this == null) return 0
    var h = 1
    for (e in this) h = 31 * h + e.hashCode()
    return h
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
@kotlin.internal.InlineOnly
public actual inline fun ByteArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun ShortArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun IntArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun LongArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun FloatArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun DoubleArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun BooleanArray?.contentToString(): String {
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
@kotlin.internal.InlineOnly
public actual inline fun CharArray?.contentToString(): String {
    if (this == null) return "null"
    val sb = StringBuilder("[")
    for (i in indices) {
        if (i > 0) sb.append(", ")
        sb.append(this[i])
    }
    sb.append("]")
    return sb.toString()
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
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.copyInto(destination: Array<T>, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): Array<T> {
    @Suppress("UNCHECKED_CAST")
    (this as Array<T>).nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.copyInto(destination: ByteArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ByteArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.copyInto(destination: ShortArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ShortArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.copyInto(destination: IntArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): IntArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.copyInto(destination: LongArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): LongArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.copyInto(destination: FloatArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): FloatArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.copyInto(destination: DoubleArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): DoubleArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.copyInto(destination: BooleanArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): BooleanArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.copyInto(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): CharArray {
    this.nativeArrayCopy(startIndex, destination, destinationOffset, endIndex - startIndex)
    return destination
}

// copyOf(): System.Array.Clone() returns a shallow copy whose runtime element type matches the receiver (no reified T
// allocation needed). The @ClrIntrinsic("Clone") receiver erases to object; ilemit resolves Array.Clone + casts the receiver.
@kotlin.clr.ClrIntrinsic("Clone")
private fun <T> Array<T>.nativeClone(): Any = TODO("clr binding should be implemented")

public actual fun <T> Array<T>.copyOf(): Array<T> = nativeClone() as Array<T>

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOf(): ByteArray = ByteArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOf(): ShortArray = ShortArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOf(): IntArray = IntArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOf(): LongArray = LongArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOf(): FloatArray = FloatArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOf(): DoubleArray = DoubleArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOf(): BooleanArray = BooleanArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOf(): CharArray = CharArray(this.size) { this[it] }

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOf(newSize: Int): ByteArray = ByteArray(newSize) { if (it < this.size) this[it] else 0.toByte() }

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOf(newSize: Int): ShortArray = ShortArray(newSize) { if (it < this.size) this[it] else 0.toShort() }

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOf(newSize: Int): IntArray = IntArray(newSize) { if (it < this.size) this[it] else 0 }

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOf(newSize: Int): LongArray = LongArray(newSize) { if (it < this.size) this[it] else 0L }

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOf(newSize: Int): FloatArray = FloatArray(newSize) { if (it < this.size) this[it] else 0.0f }

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOf(newSize: Int): DoubleArray = DoubleArray(newSize) { if (it < this.size) this[it] else 0.0 }

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOf(newSize: Int): BooleanArray = BooleanArray(newSize) { if (it < this.size) this[it] else false }

// Kotlin pads a grown CharArray with the NULL char '\u0000' (matches JVM Arrays.copyOf zero-fill), not a space (#128).
@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOf(newSize: Int): CharArray = CharArray(newSize) { if (it < this.size) this[it] else '\u0000' }

// copyOf(newSize) HONESTLY returns `Array<T?>` (extra slots are null), and `Array<X?>` is `object[]` exactly when `X`
// may be a value type (#86 D2) — a REFERENCE element keeps its typed array, so `Array<String?>` is a `string[]`. This
// generic body cannot spell either statically (there is no `T : struct` constraint and no reified `T`), so it decides
// the same way at RUNTIME, off the receiver's own element type: a value element allocates `object[]`, a reference
// element allocates `elem[]`. Both inhabit the erased `object[]` the declaration states — `string[]` by reference-array
// covariance — while a consumer at `T = String` gets the real `string[]` its `Array<String?>` slot names.
//
// The prefix is copied through `System.Array.SetValue`, which BOXES a value element into an object slot; `Array.Copy`
// would not (it refuses an `int32[]` -> `object[]` element conversion). The null tail is free — CreateInstance
// zero-fills, and a zeroed `object` slot IS a null. Exact for Int/Long/Double/Char AND reference T, with no
// per-element-type special-casing.
@kotlin.clr.ClrTypeAlias("System.Type")
private interface DotktType {
    @kotlin.clr.ClrIntrinsic("GetElementType") fun getElementType(): DotktType
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "IsValueType") fun isValueType(): Boolean
}

@kotlin.clr.ClrTypeAlias("System.Array")
private interface DotktArray {
    @kotlin.clr.ClrIntrinsic("SetValue") fun setValue(value: Any?, index: Int): Unit
}

@kotlin.clr.ClrIntrinsic("GetType")                            // object.GetType() -> the receiver's runtime array Type (e.g. int[])
private fun Any.dotktRuntimeType(): DotktType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Type.GetType")                // static Type.GetType(string) — resolves System.Object from CoreLib
private fun dotktTypeNamed(name: String): DotktType = TODO("clr binding should be implemented")

// DECLARES `Array<T?>` so the reflectively-allocated array flows to `result`/`return` with NO `as`-cast node: a cast
// target would be rewritten to the erased `object[]`, and a `string[]` reaching it as a castclass rather than as the
// covariant assignment it is would be an unnecessary check on the reference path.
@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")        // static Array.CreateInstance(Type, int) -> Array
private fun <T> dotktNewArrayOfType(elementType: DotktType, length: Int): Array<T?> = TODO("clr binding should be implemented")

public actual fun <T> Array<T>.copyOf(newSize: Int): Array<T?> {
    val elem = (this as Any).dotktRuntimeType().getElementType()
    val outElem = if (elem.isValueType()) dotktTypeNamed("System.Object") else elem
    val result: Array<T?> = dotktNewArrayOfType(outElem, newSize)
    val limit = if (newSize < this.size) newSize else this.size
    for (i in 0 until limit) (result as DotktArray).setValue(this[i], i)
    return result
}

// RUNTIME-ELEMENT-TYPE-PRESERVING sub-range copy (#117). A pure-Kotlin `arrayOfNulls<T>(n) as Array<T>` allocates an
// `Array<T?>` — `Nullable<T>[]` when inlined at a value-type call site, or an object-erased slot in a non-inline generic
// body — then REINTERPRET-casts it to a non-null `T[]`: value-type garbage / InvalidCast. A non-null `Array<T>` whose
// runtime element type equals the RECEIVER's cannot be produced by a `newarr !T`-style Kotlin allocation. So, like the
// no-arg `copyOf()` (System.Array.Clone), copyOfRange reflects on the receiver's actual runtime array type:
//   dest = System.Array.CreateInstanceFromArrayType(this.GetType(), length); System.Array.Copy(this, from, dest, 0, len)
// This is EXACT for Int/Long/Double/Char AND reference T, with no per-element-type special-casing.
@kotlin.clr.ClrIntrinsic("GetType")                                     // object.GetType() -> the receiver's runtime array Type (e.g. int[])
private fun Any.nativeGetType(): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstanceFromArrayType")    // (Type arrayType, int length) -> Array; receiver(arrayType) -> arg0 (net10.0)
private fun Any.nativeCreateArrayLike(length: Int): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.Copy")                           // Copy(src, srcIndex, dst, dstIndex, length); receiver(src) -> arg0
private fun <T> Array<T>.nativeArrayCopy(sourceIndex: Int, dest: Array<T>, destIndex: Int, length: Int): Unit = TODO("clr binding should be implemented")

public actual fun <T> Array<T>.copyOfRange(fromIndex: Int, toIndex: Int): Array<T> {
    val length = toIndex - fromIndex
    @Suppress("UNCHECKED_CAST")
    val dest = (this as Any).nativeGetType().nativeCreateArrayLike(length) as Array<T>
    this.nativeArrayCopy(fromIndex, dest, 0, length)
    return dest
}

@kotlin.internal.InlineOnly
public actual inline fun ByteArray.copyOfRange(fromIndex: Int, toIndex: Int): ByteArray = ByteArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun ShortArray.copyOfRange(fromIndex: Int, toIndex: Int): ShortArray = ShortArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun IntArray.copyOfRange(fromIndex: Int, toIndex: Int): IntArray = IntArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun LongArray.copyOfRange(fromIndex: Int, toIndex: Int): LongArray = LongArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun FloatArray.copyOfRange(fromIndex: Int, toIndex: Int): FloatArray = FloatArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun DoubleArray.copyOfRange(fromIndex: Int, toIndex: Int): DoubleArray = DoubleArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun BooleanArray.copyOfRange(fromIndex: Int, toIndex: Int): BooleanArray = BooleanArray(toIndex - fromIndex) { this[fromIndex + it] }

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOfRange(fromIndex: Int, toIndex: Int): CharArray = CharArray(toIndex - fromIndex) { this[fromIndex + it] }

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

// `plus` returns a NON-null `Array<T>`, a genuine `!T[]`, so it allocates the way `copyOfRange` does: from the
// RECEIVER's own runtime array type, which preserves the exact element type at every instantiation. The
// `arrayOfNulls<T>(n) … as Array<T>` shape these bodies used to carry cannot serve any more — `arrayOfNulls` honestly
// returns `Array<T?>` = `object[]` (#86 D2) and `object[]` is not castable to `int32[]`. This non-reified body also
// cannot use `Array<T>(n) { … }`: the frontend rejects `T` as a reified type argument. Allocating from the receiver's
// runtime type preserves the exact element type. The same reasoning applies to every generic `plus` overload below.
public actual operator fun <T> Array<T>.plus(element: T): Array<T> {
    @Suppress("UNCHECKED_CAST")
    val result = (this as Any).nativeGetType().nativeCreateArrayLike(this.size + 1) as Array<T>
    this.nativeArrayCopy(0, result, 0, this.size)
    result[this.size] = element
    return result
}

public actual operator fun ByteArray.plus(element: Byte): ByteArray = ByteArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun ShortArray.plus(element: Short): ShortArray = ShortArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun IntArray.plus(element: Int): IntArray = IntArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun LongArray.plus(element: Long): LongArray = LongArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun FloatArray.plus(element: Float): FloatArray = FloatArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun DoubleArray.plus(element: Double): DoubleArray = DoubleArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun BooleanArray.plus(element: Boolean): BooleanArray = BooleanArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun CharArray.plus(element: Char): CharArray = CharArray(this.size + 1) { if (it < this.size) this[it] else element }

@Suppress("UNCHECKED_CAST")
public actual operator fun <T> Array<T>.plus(elements: Collection<T>): Array<T> {
    val result = (this as Any).nativeGetType().nativeCreateArrayLike(this.size + elements.size) as Array<T>
    this.nativeArrayCopy(0, result, 0, this.size)
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun ByteArray.plus(elements: Collection<Byte>): ByteArray {
    val result = ByteArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun ShortArray.plus(elements: Collection<Short>): ShortArray {
    val result = ShortArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun IntArray.plus(elements: Collection<Int>): IntArray {
    val result = IntArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun LongArray.plus(elements: Collection<Long>): LongArray {
    val result = LongArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun FloatArray.plus(elements: Collection<Float>): FloatArray {
    val result = FloatArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun DoubleArray.plus(elements: Collection<Double>): DoubleArray {
    val result = DoubleArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun BooleanArray.plus(elements: Collection<Boolean>): BooleanArray {
    val result = BooleanArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

public actual operator fun CharArray.plus(elements: Collection<Char>): CharArray {
    val result = CharArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result
}

@Suppress("UNCHECKED_CAST")
public actual operator fun <T> Array<T>.plus(elements: Array<out T>): Array<T> {
    val result = (this as Any).nativeGetType().nativeCreateArrayLike(this.size + elements.size) as Array<T>
    this.nativeArrayCopy(0, result, 0, this.size)
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun ByteArray.plus(elements: ByteArray): ByteArray {
    val result = ByteArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun ShortArray.plus(elements: ShortArray): ShortArray {
    val result = ShortArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun IntArray.plus(elements: IntArray): IntArray {
    val result = IntArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun LongArray.plus(elements: LongArray): LongArray {
    val result = LongArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun FloatArray.plus(elements: FloatArray): FloatArray {
    val result = FloatArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun DoubleArray.plus(elements: DoubleArray): DoubleArray {
    val result = DoubleArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun BooleanArray.plus(elements: BooleanArray): BooleanArray {
    val result = BooleanArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

public actual operator fun CharArray.plus(elements: CharArray): CharArray {
    val result = CharArray(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result
}

@kotlin.internal.InlineOnly
public actual inline fun <T> Array<T>.plusElement(element: T): Array<T> = this + element

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun IntArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun LongArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun ByteArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun ShortArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun DoubleArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun FloatArray.sort(): Unit { TODO("clr binding should be implemented") }

@kotlin.clr.ClrIntrinsic("System.Array.Sort")
public actual fun CharArray.sort(): Unit { TODO("clr binding should be implemented") }

public actual fun <T : Comparable<T>> Array<out T>.sort(): Unit {
    // Stable O(n log n) bottom-up merge sort (the @Clr System.Array.Sort overload routing failed at runtime --
    // EntryPointNotFound). Array<out T> is cast to Array<T> to write back (same object); aux is Array<Any?> as in
    // MutableList.sortWith. @See [[stdlib-binding-status-and-pow-gap]] for the hand-written-sort perf note (#5).
    val n = size
    if (n < 2) return
    @Suppress("UNCHECKED_CAST") val a = this as Array<T>
    val aux = arrayOfNulls<Any?>(n)
    var width = 1
    while (width < n) {
        var lo = 0
        while (lo < n) {
            val mid = if (lo + width < n) lo + width else n
            val hi = if (lo + 2 * width < n) lo + 2 * width else n
            var l = lo; var r = mid; var k = lo
            while (l < mid && r < hi) {
                if (a[l] <= a[r]) { aux[k] = a[l]; l = l + 1 } else { aux[k] = a[r]; r = r + 1 }
                k = k + 1
            }
            while (l < mid) { aux[k] = a[l]; l = l + 1; k = k + 1 }
            while (r < hi) { aux[k] = a[r]; r = r + 1; k = k + 1 }
            var j = lo
            @Suppress("UNCHECKED_CAST") while (j < hi) { a[j] = aux[j] as T; j = j + 1 }
            lo = lo + 2 * width
        }
        width = width * 2
    }
}

// Thin wrapper: Kotlin sort(fromIndex, toIndex) has an exclusive end index, while
// .NET System.Array.Sort(array, index, length) takes a length. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun <T : Comparable<T>> Array<out T>.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@SinceKotlin("1.4")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T : Comparable<T>> Array<out T>.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun ByteArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun ShortArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun IntArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun LongArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun FloatArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun DoubleArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// Adapt exclusive toIndex -> .NET length: System.Array.Sort(Array, index, length).
@kotlin.clr.ClrIntrinsic("System.Array.Sort")
private fun CharArray.nativeSort(index: Int, length: Int): Unit = TODO("@Clr System.Array.Sort(Array,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.sort(fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeSort(fromIndex, toIndex - fromIndex)
}

// In-place stable insertion sort using the comparator. The .NET `Comparator<T>` is a `fun interface`
// (not `IComparer`), so `System.Array.Sort(IComparer)` is unavailable; we sort directly. Insertion sort
// is correct, stable, and in-place (no temp array, hence no reified element type). The receiver is
// `Array<out T>`, whose `set` is unavailable through the `out` projection, so we cast to `Array<T>`
// (file-level @Suppress("UNCHECKED_CAST")) to write the reordered elements back.
// The .NET Comparator is a `fun interface` (not IComparer), so we sort directly. A STABLE O(n log n) bottom-up merge
// sort with an Any?[] aux (`Array<Any?>(n){null}` -> object[]) + the file-level @Suppress("UNCHECKED_CAST"). Stable: a
// left run wins ties (`<= 0`).
internal fun <T> mergeSortArray(a: Array<T>, fromIndex: Int, toIndex: Int, comparator: Comparator<in T>) {
    val n = toIndex - fromIndex
    if (n < 2) return
    val tmp = Array<Any?>(n) { null }
    var width = 1
    while (width < n) {
        var lo = 0
        while (lo < n) {
            val mid = if (lo + width < n) lo + width else n
            val hi = if (lo + 2 * width < n) lo + 2 * width else n
            var l = lo; var r = mid; var k = lo
            while (l < mid && r < hi) {
                if (comparator.compare(a[fromIndex + l], a[fromIndex + r]) <= 0) { tmp[k] = a[fromIndex + l]; l = l + 1 }
                else { tmp[k] = a[fromIndex + r]; r = r + 1 }
                k = k + 1
            }
            while (l < mid) { tmp[k] = a[fromIndex + l]; l = l + 1; k = k + 1 }
            while (r < hi) { tmp[k] = a[fromIndex + r]; r = r + 1; k = k + 1 }
            var j = lo
            while (j < hi) { a[fromIndex + j] = tmp[j] as T; j = j + 1 }
            lo = lo + 2 * width
        }
        width = width * 2
    }
}

public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>): Unit =
    mergeSortArray(this as Array<T>, 0, this.size, comparator)

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.sortWith(comparator: Comparator<in T>, fromIndex: Int = 0, toIndex: Int = size): Unit =
    mergeSortArray(this as Array<T>, fromIndex, toIndex, comparator)

public actual fun ByteArray.toTypedArray(): Array<Byte> = Array(size) { this[it] }

public actual fun ShortArray.toTypedArray(): Array<Short> = Array(size) { this[it] }

public actual fun IntArray.toTypedArray(): Array<Int> = Array(size) { this[it] }

public actual fun LongArray.toTypedArray(): Array<Long> = Array(size) { this[it] }

public actual fun FloatArray.toTypedArray(): Array<Float> = Array(size) { this[it] }

public actual fun DoubleArray.toTypedArray(): Array<Double> = Array(size) { this[it] }

public actual fun BooleanArray.toTypedArray(): Array<Boolean> = Array(size) { this[it] }

public actual fun CharArray.toTypedArray(): Array<Char> = Array(size) { this[it] }
