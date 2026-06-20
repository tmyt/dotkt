// IL parity: user-defined extension functions (receiver -> __self first param).
fun Int.triple(): Int = this * 3
fun String.shout(): String = this.uppercase()
fun main() {
    println(7.triple())
    println("hi".shout())
}
