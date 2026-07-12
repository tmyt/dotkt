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

// Lightweight read-only List VIEW backed by the array. `AbstractList<out E>` only requires `size` + `get`
// (its `iterator()` etc. are concrete), so we override those plus `contains`/`indexOf`/`lastIndexOf` for a
// direct array scan. No copy is made: the view reflects later writes to the backing array.
public actual fun <T> Array<out T>.asList(): List<T> { val r = ArrayList<T>(); for (e in this) r.add(e); return r }

public actual fun ByteArray.asList(): List<Byte> { val r = ArrayList<Byte>(); for (e in this) r.add(e); return r }

public actual fun ShortArray.asList(): List<Short> { val r = ArrayList<Short>(); for (e in this) r.add(e); return r }

public actual fun IntArray.asList(): List<Int> { val r = ArrayList<Int>(); for (e in this) r.add(e); return r }

public actual fun LongArray.asList(): List<Long> { val r = ArrayList<Long>(); for (e in this) r.add(e); return r }

public actual fun FloatArray.asList(): List<Float> { val r = ArrayList<Float>(); for (e in this) r.add(e); return r }

public actual fun DoubleArray.asList(): List<Double> { val r = ArrayList<Double>(); for (e in this) r.add(e); return r }

public actual fun BooleanArray.asList(): List<Boolean> { val r = ArrayList<Boolean>(); for (e in this) r.add(e); return r }

public actual fun CharArray.asList(): List<Char> { val r = ArrayList<Char>(); for (e in this) r.add(e); return r }

// contentDeep{Equals,HashCode,ToString} are `inline` recursive ops over nested arrays. Since an `inline` function
// cannot recurse, each public inline entry point delegates to a non-inline `@PublishedApi internal` recursive helper
// below. Element dispatch covers `is Array<*>` plus the 9 primitive array kinds (reusing the primitive content ops
// defined in this file). NOTE: cyclic (self-referential) arrays are NOT detected and would recurse infinitely; this
// is acceptable as such arrays are extremely rare.
@PublishedApi internal fun contentDeepToStringImpl(arr: Array<*>?): String {
    if (arr == null) return "null"
    val sb = StringBuilder("[")
    for (i in arr.indices) {
        if (i != 0) sb.append(", ")
        val e: Any? = arr[i]
        when (e) {
            null -> sb.append("null")
            is Array<*> -> sb.append(contentDeepToStringImpl(e))
            is ByteArray -> sb.append(e.contentToString())
            is ShortArray -> sb.append(e.contentToString())
            is IntArray -> sb.append(e.contentToString())
            is LongArray -> sb.append(e.contentToString())
            is FloatArray -> sb.append(e.contentToString())
            is DoubleArray -> sb.append(e.contentToString())
            is BooleanArray -> sb.append(e.contentToString())
            is CharArray -> sb.append(e.contentToString())
            else -> sb.append(e.toString())
        }
    }
    sb.append("]")
    return sb.toString()
}

@PublishedApi internal fun contentDeepEqualsImpl(a: Array<*>?, b: Array<*>?): Boolean {
    if (a === b) return true
    if (a == null || b == null || a.size != b.size) return false
    for (i in a.indices) {
        val x: Any? = a[i]; val y: Any? = b[i]
        val eq = when {
            x === y -> true
            x == null || y == null -> false
            x is Array<*> && y is Array<*> -> contentDeepEqualsImpl(x, y)
            x is ByteArray && y is ByteArray -> x.contentEquals(y)
            x is ShortArray && y is ShortArray -> x.contentEquals(y)
            x is IntArray && y is IntArray -> x.contentEquals(y)
            x is LongArray && y is LongArray -> x.contentEquals(y)
            x is FloatArray && y is FloatArray -> x.contentEquals(y)
            x is DoubleArray && y is DoubleArray -> x.contentEquals(y)
            x is BooleanArray && y is BooleanArray -> x.contentEquals(y)
            x is CharArray && y is CharArray -> x.contentEquals(y)
            else -> x == y
        }
        if (!eq) return false
    }
    return true
}

@PublishedApi internal fun contentDeepHashCodeImpl(arr: Array<*>?): Int {
    if (arr == null) return 0
    var h = 1
    for (e in arr) {
        val eh = when (e) {
            null -> 0
            is Array<*> -> contentDeepHashCodeImpl(e)
            is ByteArray -> e.contentHashCode()
            is ShortArray -> e.contentHashCode()
            is IntArray -> e.contentHashCode()
            is LongArray -> e.contentHashCode()
            is FloatArray -> e.contentHashCode()
            is DoubleArray -> e.contentHashCode()
            is BooleanArray -> e.contentHashCode()
            is CharArray -> e.contentHashCode()
            else -> e.hashCode()
        }
        h = 31 * h + eh
    }
    return h
}

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>.contentDeepEquals(other: Array<out T>): Boolean = contentDeepEqualsImpl(this, other)

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun <T> Array<out T>?.contentDeepEquals(other: Array<out T>?): Boolean = contentDeepEqualsImpl(this, other)

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepHashCode(): Int = contentDeepHashCodeImpl(this)

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepHashCode(): Int = contentDeepHashCodeImpl(this)

@SinceKotlin("1.1")
@kotlin.internal.LowPriorityInOverloadResolution
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>.contentDeepToString(): String = contentDeepToStringImpl(this)

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun <T> Array<out T>?.contentDeepToString(): String = contentDeepToStringImpl(this)

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
    for (i in indices) if (this[i] != other[i]) return false
    return true
}

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline infix fun DoubleArray?.contentEquals(other: DoubleArray?): Boolean {
    if (this === other) return true
    if (this == null || other == null) return false
    if (this.size != other.size) return false
    for (i in indices) if (this[i] != other[i]) return false
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

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<out T>.copyInto(destination: Array<T>, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): Array<T> {
    var i = startIndex
    var j = destinationOffset
    while (i < endIndex) {
        destination[j] = this[i]
        i++
        j++
    }
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.copyInto(destination: ByteArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ByteArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.copyInto(destination: ShortArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): ShortArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.copyInto(destination: IntArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): IntArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.copyInto(destination: LongArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): LongArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.copyInto(destination: FloatArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): FloatArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.copyInto(destination: DoubleArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): DoubleArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.copyInto(destination: BooleanArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): BooleanArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
    return destination
}

@SinceKotlin("1.3")
@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.copyInto(destination: CharArray, destinationOffset: Int = 0, startIndex: Int = 0, endIndex: Int = size): CharArray {
    for (i in startIndex until endIndex) destination[destinationOffset + (i - startIndex)] = this[i]
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

@kotlin.internal.InlineOnly
public actual inline fun CharArray.copyOf(newSize: Int): CharArray = CharArray(newSize) { if (it < this.size) this[it] else ' ' }

// copyOf(newSize) HONESTLY returns `Array<T?>` (extra slots are null). For a value-type T the canonical runtime
// representation of `Array<T?>` is `Nullable<T>[]` (#113). This GENERIC body cannot allocate `Nullable<!T>[]`
// statically (there is no `T : struct` constraint), and a plain `arrayOfNulls<T>(newSize)` here collapses to a bare
// `newarr !T` (an `int[]`) — bir2cir's ReferenceNullableStrip strips `Nullable(Tv)` on an OPEN type-variable, which is
// LOAD-BEARING for the plus/toTypedArray reify-back siblings but WRONG for this `Array<T?>`-returning site (#124): the
// `int[]` corrupts on a `Nullable<int>` read and fails the consumer's `as Array<T?>` cast. Unlike plus/copyOfRange,
// copyOf(newSize) is the only generic-body `arrayOfNulls<T>` site whose result escapes as `Array<T?>`, so it must build
// the result by RUNTIME reflection on the receiver's element type — the value-type sibling of the no-arg copyOf
// (Array.Clone) and copyOfRange (#117): allocate `Nullable<elem>[]` for a value-type elem (else `elem[]`) and
// per-element SetValue the prefix. CLR nullable boxing lifts a boxed T into a `Nullable<T>` slot; `Array.Copy` does NOT
// lift. The null tail is free — CreateInstance zero-fills (HasValue=false / null ref). Exact for Int/Long/Double/Char
// AND reference T, no per-element-type special-casing.
@kotlin.clr.ClrTypeAlias("System.Type")
private interface DotktType {
    @kotlin.clr.ClrIntrinsic("GetElementType") fun getElementType(): DotktType
    @kotlin.clr.ClrIntrinsic("MakeGenericType") fun makeGenericType(typeArguments: Array<DotktType>): DotktType
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "IsValueType") fun isValueType(): Boolean
}

@kotlin.clr.ClrTypeAlias("System.Array")
private interface DotktArray {
    @kotlin.clr.ClrIntrinsic("SetValue") fun setValue(value: Any?, index: Int): Unit
}

@kotlin.clr.ClrIntrinsic("GetType")                            // object.GetType() -> the receiver's runtime array Type (e.g. int[])
private fun Any.dotktRuntimeType(): DotktType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Type.GetType")                // static Type.GetType(string) — resolves the open `System.Nullable`1` from CoreLib
private fun dotktTypeNamed(name: String): DotktType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Nullable.GetUnderlyingType")  // static; non-null iff `t` is already a `Nullable<X>` (avoid a Nullable<Nullable<X>> double-wrap)
private fun dotktNullableUnderlying(t: DotktType): DotktType? = TODO("clr binding should be implemented")

// DECLARES `Array<T?>` so the reflectively-allocated array flows to `result`/`return` with NO `as`-cast node: the
// blanket `Array(Nullable(Tv))` erasure would rewrite a cast target to `object[]`, and a value-type `Nullable<int>[]`
// is NOT castclass-able to `object[]` (struct-element arrays are not covariant) — an InvalidCast on the fixed path.
@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")        // static Array.CreateInstance(Type, int) -> Array
private fun <T> dotktNewArrayOfType(elementType: DotktType, length: Int): Array<T?> = TODO("clr binding should be implemented")

public actual fun <T> Array<T>.copyOf(newSize: Int): Array<T?> {
    val elem = (this as Any).dotktRuntimeType().getElementType()
    val outElem = if (dotktNullableUnderlying(elem) != null) elem                                    // receiver already `Nullable<X>[]`
                  else if (elem.isValueType()) dotktTypeNamed("System.Nullable`1").makeGenericType(arrayOf(elem))
                  else elem
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

// Thin wrapper: Kotlin fill(element, fromIndex, toIndex) has an exclusive end index, while
// .NET System.Array.Fill(array, value, startIndex, count) takes a count. Adapt by subtracting.
@kotlin.clr.ClrIntrinsic("System.Array.Fill")
private fun <T> Array<T>.nativeFill(element: T, startIndex: Int, count: Int): Unit = TODO("@Clr System.Array.Fill(T[],T,int,int)")

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun <T> Array<T>.fill(element: T, fromIndex: Int = 0, toIndex: Int = size): Unit {
    nativeFill(element, fromIndex, toIndex - fromIndex)
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ByteArray.fill(element: Byte, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun ShortArray.fill(element: Short, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun IntArray.fill(element: Int, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun LongArray.fill(element: Long, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun FloatArray.fill(element: Float, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun DoubleArray.fill(element: Double, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun BooleanArray.fill(element: Boolean, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

@Suppress("ACTUAL_FUNCTION_WITH_DEFAULT_ARGUMENTS")
public actual fun CharArray.fill(element: Char, fromIndex: Int = 0, toIndex: Int = size): Unit {
    for (i in fromIndex until toIndex) this[i] = element
}

public actual operator fun <T> Array<T>.plus(element: T): Array<T> {
    val result = arrayOfNulls<T>(this.size + 1)
    for (i in this.indices) result[i] = this[i]
    result[this.size] = element
    return result as Array<T>
}

public actual operator fun ByteArray.plus(element: Byte): ByteArray = ByteArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun ShortArray.plus(element: Short): ShortArray = ShortArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun IntArray.plus(element: Int): IntArray = IntArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun LongArray.plus(element: Long): LongArray = LongArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun FloatArray.plus(element: Float): FloatArray = FloatArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun DoubleArray.plus(element: Double): DoubleArray = DoubleArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun BooleanArray.plus(element: Boolean): BooleanArray = BooleanArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun CharArray.plus(element: Char): CharArray = CharArray(this.size + 1) { if (it < this.size) this[it] else element }

public actual operator fun <T> Array<T>.plus(elements: Collection<T>): Array<T> {
    val result = arrayOfNulls<T>(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    var index = this.size
    for (e in elements) {
        result[index] = e
        index++
    }
    return result as Array<T>
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

public actual operator fun <T> Array<T>.plus(elements: Array<out T>): Array<T> {
    val result = arrayOfNulls<T>(this.size + elements.size)
    for (i in this.indices) result[i] = this[i]
    for (i in elements.indices) result[this.size + i] = elements[i]
    return result as Array<T>
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
