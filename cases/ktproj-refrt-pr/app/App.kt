import mylib.greeting
import mylib.sumTo
fun main() {
    println(greeting("world"))
    println(sumTo(10))
    val xs = listOf("x", "y", "z")
    println(xs.size)
    println(greeting(xs[2]))
}
