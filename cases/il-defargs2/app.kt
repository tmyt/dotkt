// Same-module default argument whose value references ANOTHER value parameter (`b = a * 10`, `c = a + b`).
fun f(a: Int, b: Int = a * 10): Int = a + b
fun g(x: Int, y: Int = x + 1): Int = x * y
fun h(a: Int, b: Int = 3, c: Int = a + b): Int = a * 100 + b * 10 + c
fun main() {
    println(f(5))         // 55
    println(f(5, 2))      // 7
    println(g(3))         // 12
    println(g(3, 10))     // 30
    println(h(1))         // 134
    println(h(1, 5))      // 156
    println(h(1, 5, 9))   // 159
}
