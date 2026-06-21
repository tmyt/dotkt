// Function-local classes: lifted to top-level synthetic types; captured outer locals become ctor params.
fun main() {
    class L(val n: Int) { fun d() = n * 2 }
    println(L(5).d())                       // 10
    println(L(21).d())                      // 42   (multiple instances of one local class)

    val k = 100
    class Cap { fun g() = k + 1 }           // captures the outer local `k`
    println(Cap().g())                      // 101

    data class P(val x: Int, val y: Int)    // local data class (componentN / equals work)
    val p = P(3, 4)
    println("${p.x},${p.y}")                // 3,4
    println(p == P(3, 4))                   // true

    var total = 0
    for (i in 1..3) { class Row(val v: Int); total += Row(i * 10).v }   // local class declared in a loop
    println(total)                          // 60
}
