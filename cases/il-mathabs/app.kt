// C9: kotlin.math.abs WRAPS at MIN_VALUE (unchecked negation), it does NOT throw like
// System.Math.Abs's checked overload. abs(Int.MIN_VALUE) == Int.MIN_VALUE.
import kotlin.math.abs
fun main() {
    println(abs(Int.MIN_VALUE))
    println(abs(Long.MIN_VALUE))
    println(abs(-5))
    println(abs(-5L))
    println(abs(0))
    println(abs(Int.MAX_VALUE))
}
