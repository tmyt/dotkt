// IL parity: coerceAtMost/coerceAtLeast/coerceIn run the pure-Kotlin stdlib bodies (NO kotc System.Math lowering).
fun main() {
    println(10.coerceAtMost(7))    // 7
    println(3.coerceAtLeast(5))    // 5
    println(8.coerceIn(1, 5))      // 5
    println(2.coerceIn(1, 5))      // 2
    println(0.coerceIn(1, 5))      // 1
    println(8.coerceIn(1..5))      // 5  (progression-range form)
    println(7L.coerceAtMost(10L))  // 7
}
