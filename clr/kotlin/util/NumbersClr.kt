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
@kotlin.clr.ClrIntrinsic("System.BitConverter.DoubleToInt64Bits")
public actual fun Double.toBits(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.DoubleToInt64Bits")
public actual fun Double.toRawBits(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.Int64BitsToDouble")
public actual fun Double.Companion.fromBits(bits: Long): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.SingleToInt32Bits")
public actual fun Float.toBits(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.clr.ClrIntrinsic("System.BitConverter.SingleToInt32Bits")
public actual fun Float.toRawBits(): Int = TODO("clr binding should be implemented")

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
public actual inline fun Int.takeHighestOneBit(): Int = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Int.takeLowestOneBit(): Int = TODO("clr binding should be implemented")

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
public actual inline fun Long.takeHighestOneBit(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
@kotlin.internal.InlineOnly
public actual inline fun Long.takeLowestOneBit(): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateLeft")
public actual fun Long.rotateLeft(bitCount: Int): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.6")
@kotlin.clr.ClrIntrinsic("System.Numerics.BitOperations.RotateRight")
public actual fun Long.rotateRight(bitCount: Int): Long = TODO("clr binding should be implemented")
