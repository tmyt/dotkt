// IL parity: scope functions let/run/with/also/apply -> inlined value-blocks (no delegate).
fun main() {
    println(5.let { it -> it * 2 })
    println(5.run { this + 1 })
    println(with(3) { this * this })
    println(10.also { println(it) })
    println(7.apply { })
}
