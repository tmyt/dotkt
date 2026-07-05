// C10: Int/Long MIN_VALUE / -1 and % -1 must WRAP (Kotlin), not throw OverflowException (raw CIL div/rem).
fun main() {
    println(Int.MIN_VALUE / -1)      // -2147483648 (wraps)
    println(Int.MIN_VALUE % -1)      // 0
    println(Long.MIN_VALUE / -1L)    // -9223372036854775808
    println(Long.MIN_VALUE % -1L)    // 0
    println(7 / -1)                  // -7
    println(7 % -1)                  // 0
    println(-13 / 4)                 // -3
    println(-13 % 4)                 // -1
    println(10 / 3)                  // 3
    println(10 % 3)                  // 1
    val a = -2147483648; val b = -1
    println(a / b)                   // -2147483648 (variable path)
    println(a % b)                   // 0
}
