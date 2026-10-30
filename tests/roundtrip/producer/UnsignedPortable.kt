package roundtrip.unsignedportable

fun formatUInt(value: UInt, radix: Int): String = value.toString(radix)
fun formatULong(value: ULong, radix: Int): String = value.toString(radix)
fun savedUIntFormatter(): (UInt, Int) -> String = UInt::toString
fun savedULongFormatter(): (ULong, Int) -> String = ULong::toString
fun nativeUIntMax(a: UInt, b: UInt): UInt = System.Math.Max(a, b)
fun nativeULongMax(a: ULong, b: ULong): ULong = System.Math.Max(a, b)
inline fun observedFormat(value: ULong, radix: Int, block: (String) -> String): String = block(value.toString(radix))
inline fun observedUInt(value: Double, block: (UInt) -> UInt): UInt = block(value.toUInt())
inline fun observedULong(value: Double, block: (ULong) -> ULong): ULong = block(value.toULong())
