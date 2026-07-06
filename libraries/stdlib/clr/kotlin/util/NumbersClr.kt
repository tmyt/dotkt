@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

@kotlin.clr.ClrIntrinsic("System.Double.IsNaN")
public actual fun Double.isNaN(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Single.IsNaN")
public actual fun Float.isNaN(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Double.IsInfinity")
public actual fun Double.isInfinite(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Single.IsInfinity")
public actual fun Float.isInfinite(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Double.IsFinite")
public actual fun Double.isFinite(): Boolean = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Single.IsFinite")
public actual fun Float.isFinite(): Boolean = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Double.toBits(): Long = if (this.isNaN()) 0x7ff8000000000000L else this.toRawBits()

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.DoubleToInt64Bits")
public actual fun Double.toRawBits(): Long = TODO("clr binding should be implemented")

// TODO(clr): companion receiver prepend, needs compiler fix
// The @ClrIntrinsic clrName('.') split prepends the companion receiver, emitting a 2-arg
// Int64BitsToDouble(companion, bits) call. The static bind cannot be fixed stdlib-side; keep TODO.
@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.Int64BitsToDouble")
public actual fun Double.Companion.fromBits(bits: Long): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
public actual fun Float.toBits(): Int = if (this.isNaN()) 0x7fc00000 else this.toRawBits()

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.SingleToInt32Bits")
public actual fun Float.toRawBits(): Int = TODO("clr binding should be implemented")

// TODO(clr): companion receiver prepend, needs compiler fix
// The @ClrIntrinsic clrName('.') split prepends the companion receiver, emitting a 2-arg
// Int32BitsToSingle(companion, bits) call. The static bind cannot be fixed stdlib-side; keep TODO.
@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.Int32BitsToSingle")
public actual fun Float.Companion.fromBits(bits: Int): Float = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.PopCount")
public actual fun Int.countOneBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.LeadingZeroCount")
public actual fun Int.countLeadingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.TrailingZeroCount")
public actual fun Int.countTrailingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Int.takeHighestOneBit(): Int =
    if (this == 0) 0 else 1 shl (31 - this.countLeadingZeroBits())

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Int.takeLowestOneBit(): Int = this and -this

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateLeft")
public actual fun Int.rotateLeft(bitCount: Int): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateRight")
public actual fun Int.rotateRight(bitCount: Int): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.PopCount")
public actual fun Long.countOneBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.LeadingZeroCount")
public actual fun Long.countLeadingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.TrailingZeroCount")
public actual fun Long.countTrailingZeroBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Long.takeHighestOneBit(): Long =
    if (this == 0L) 0L else 1L shl (63 - this.countLeadingZeroBits())

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Long.takeLowestOneBit(): Long = this and -this

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateLeft")
public actual fun Long.rotateLeft(bitCount: Int): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateRight")
public actual fun Long.rotateRight(bitCount: Int): Long = TODO("clr binding should be implemented")

// ---- Double/Float total-order equality & comparison (C14) ----
// Kotlin contracts a TOTAL order on Double/Float that differs from IEEE (and from System.Double.Equals /
// System.Double.CompareTo): `-0.0` sorts strictly below `0.0`, and `NaN` is the LARGEST value with `NaN == NaN`
// (a single canonical NaN). The primitive `==`/`<`/`>` operators keep the fast IEEE semantics; these helpers are
// what a BOXED `==` (`kotlin.Any.equals` on a boxed floating value) and `Comparable.compareTo` route to, so
// structural equality and ordered comparison follow Kotlin, not the CLR default. The bit compare uses `toBits()`
// (NaN-canonicalizing) — for `-0.0` the sign bit makes its Long/Int pattern negative, hence below `0.0`.

/** Kotlin total-order equality of two Double values: `NaN == NaN`, `-0.0 != 0.0`. */
public fun clrDoubleEquals(a: Double, b: Double): Boolean = a.toBits() == b.toBits()

/** Kotlin total-order equality of two Float values: `NaN == NaN`, `-0.0f != 0.0f`. */
public fun clrFloatEquals(a: Float, b: Float): Boolean = a.toBits() == b.toBits()

/** Kotlin total-order comparison of two Double values (`-0.0 < 0.0`, NaN largest, `NaN.compareTo(NaN) == 0`). */
public fun clrDoubleCompare(a: Double, b: Double): Int {
    if (a < b) return -1
    if (a > b) return 1
    val ab = a.toBits()
    val bb = b.toBits()
    return if (ab == bb) 0 else if (ab < bb) -1 else 1
}

/** Kotlin total-order comparison of two Float values (`-0.0f < 0.0f`, NaN largest, `NaN.compareTo(NaN) == 0`). */
public fun clrFloatCompare(a: Float, b: Float): Int {
    if (a < b) return -1
    if (a > b) return 1
    val ab = a.toBits()
    val bb = b.toBits()
    return if (ab == bb) 0 else if (ab < bb) -1 else 1
}
