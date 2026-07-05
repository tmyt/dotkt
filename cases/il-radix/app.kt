// C4: Int/Long.toString(radix) — sign + arbitrary base, no two's-complement, no crash on base 36.
fun main() {
    println((-255).toString(16))
    println(255.toString(16))
    println(Int.MIN_VALUE.toString(16))
    println(35.toString(36))
    println(255L.toString(16))
    println((-255L).toString(16))
    println(10.toString(2))
}
