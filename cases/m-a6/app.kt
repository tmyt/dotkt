// A: companion object — const (inlined), non-const val (static field), and factory method (static).
class Circle(val r: Double) {
    companion object {
        const val PI = 3.14
        val NAME = "circle"
        fun unit(): Circle = Circle(1.0)
    }
    fun area(): Double = PI * r * r
}

fun main() {
    println(Circle.PI)
    println(Circle.NAME)
    val u = Circle.unit()
    println(u.area())
}
