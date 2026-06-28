import mylib.greeting
fun main() {
    println(greeting("world"))
    val xs = listOf("x", "y", "z")
    println(xs.size)
    println(greeting(xs[1]))
}
