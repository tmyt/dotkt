// `@JvmInline value class` (passed/returned/method-bearing) and `Int/Long.toString(radix)`.
@JvmInline value class Money(val cents: Int) {
    fun dollars(): Int = cents / 100
}
fun fee(m: Money): Int = m.cents

fun main() {
    val m = Money(1250)
    println(m.cents)            // 1250
    println(m.dollars())       // 12
    println(fee(m))            // 1250  (passed as an argument)

    println(255.toString(16))  // ff
    println(10.toString(2))    // 1010
    println(255L.toString(16)) // ff
}
