// IL parity: kotlin.math.* -> System.Math.* (clrStatic lowering; ilemit unchanged).
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.sqrt
fun main() {
    println(abs(-9))
    println(max(3, 7))
    println(min(3, 7))
    println(sqrt(16.0))
}
