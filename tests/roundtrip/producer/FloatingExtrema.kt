package roundtrip.floatingextrema

inline fun observedFloatMax(a: Float, b: Float, block: (Float) -> Float): Float = block(maxOf(a, b))
inline fun observedFloatMin(a: Float, b: Float, block: (Float) -> Float): Float = block(minOf(a, b))
inline fun observedDoubleMax(a: Double, b: Double, block: (Double) -> Double): Double = block(maxOf(a, b))
inline fun observedDoubleMin(a: Double, b: Double, block: (Double) -> Double): Double = block(minOf(a, b))
fun savedFloatMax(): (Float, Float) -> Float = ::maxOf
fun savedFloatMin(): (Float, Float) -> Float = ::minOf
fun savedDoubleMax(): (Double, Double) -> Double = ::maxOf
fun savedDoubleMin(): (Double, Double) -> Double = ::minOf
fun floatMaxMany(a: Float, vararg other: Float): Float = maxOf(a, *other)
fun floatMinMany(a: Float, vararg other: Float): Float = minOf(a, *other)
fun doubleMaxMany(a: Double, vararg other: Double): Double = maxOf(a, *other)
fun doubleMinMany(a: Double, vararg other: Double): Double = minOf(a, *other)
