// #103: Double/Float.roundToInt/roundToLong are round-half-UP toward +inf (floor(x+0.5)), NOT banker's
// rounding (System.Math.Round default = ToEven). Ties: 0.5->1, 2.5->3, -2.5->-2, -0.5->0. NaN throws
// IllegalArgumentException; out-of-range saturates to Int/Long MIN/MAX. (kotlin.math.round stays ties-to-even.)

import kotlin.math.roundToInt
import kotlin.math.roundToLong

fun main() {
    println(2.5.roundToInt())       // 3
    println((-2.5).roundToInt())    // -2
    println(0.5.roundToInt())       // 1
    println((-0.5).roundToInt())    // 0
    println(3.5.roundToInt())       // 4
    println(2.4.roundToInt())       // 2 (not a tie)
    println(2.6.roundToInt())       // 3
    println(2.5.roundToLong())      // 3
    println((-2.5).roundToLong())   // -2
    println(2.5f.roundToInt())      // 3
    println((-2.5f).roundToInt())   // -2
    println(2.5f.roundToLong())     // 3
    // out-of-range saturates
    println(1e30.roundToInt())      // 2147483647
    println((-1e30).roundToInt())   // -2147483648
    // NaN throws IllegalArgumentException
    try { Double.NaN.roundToInt(); println("NO-THROW") }
    catch (e: IllegalArgumentException) { println("NaN-throws") }
}
