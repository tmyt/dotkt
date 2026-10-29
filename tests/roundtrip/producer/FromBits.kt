package roundtrip.frombits

fun floatFromBits(bits: Int): Float = Float.fromBits(bits)
fun doubleFromBits(bits: Long): Double = Double.fromBits(bits)
fun savedFloatFromBits(): (Int) -> Float = Float::fromBits
fun savedDoubleFromBits(): (Long) -> Double = Double::fromBits
inline fun observedFloatFromBits(bits: Int, block: (Float) -> Float): Float = block(Float.fromBits(bits))
inline fun observedDoubleFromBits(bits: Long, block: (Double) -> Double): Double = block(Double.fromBits(bits))
