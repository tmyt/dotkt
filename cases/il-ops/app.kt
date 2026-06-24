// IL parity: do-while loop, bitwise/shift ops, unary inv, numeric conversions.
fun main() {
    var i = 0
    do { i = i + 1 } while (i < 3)
    println(i)
    println(6 and 3)
    println(6 or 1)
    println(6 xor 5)
    println(1 shl 4)
    println(255 shr 4)
    println(0.inv())
    println(3.7.toInt())
    println(5.toLong())
}
