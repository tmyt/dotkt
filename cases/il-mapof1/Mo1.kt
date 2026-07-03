fun main() {
    val m = mapOf("a" to 1)                   // single-pair overload (since-1.9) — was returning an EMPTY map
    println(m.size)                           // 1
    println(m["a"])                           // 1
    println(mapOf("x" to 1, "y" to 2).size)   // 2 (vararg still works)
    println(mutableMapOf("k" to 7).size)      // 1 (mutable parity)
}
