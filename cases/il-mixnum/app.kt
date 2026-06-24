// Mixed-type numeric arithmetic coerces to the wider type (Double/Int, Int/Long, Float/Int, comparisons).
fun main() {
    val d = 8.0; val n = 3
    println(d / n)            // 2.6666666666666665
    println(n * 2 + d)        // 14.0
    println(d > n)            // true
    val i = 5; val l = 10L
    println(i + l)            // 15
    println(l - i)            // 5
    println(10 / 3)           // 3
    println(7.0 / 2.0)        // 3.5
}
