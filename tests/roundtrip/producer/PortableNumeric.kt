package roundtrip.portablenumeric

import kotlin.math.*

fun logarithm(value: Double, base: Double): Double = log(value, base)
fun logarithmFloat(value: Float, base: Float): Float = log(value, base)
fun unitInLastPlace(value: Double): Double = value.ulp
fun rotateWord(value: Int, distance: Int): Int = value.rotateLeft(distance)
fun rotateWide(value: Long, distance: Int): Long = value.rotateRight(distance)
fun savedFinite(): (Double) -> Boolean = Double::isFinite
fun savedWideRotation(): (Long, Int) -> Long = Long::rotateRight
inline fun observeLog(value: Double, block: (Double) -> Double): Double = block(log(value, 2.0))
