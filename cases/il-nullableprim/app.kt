// C1 regression: a value-type nullable (Int?/Long?/Double?) smart-cast to its non-null value must UNWRAP
// Nullable<T>.Value — NOT read HasValue / load the raw struct (which gave garbage / InvalidProgram / SIGSEGV).
// Covers: assignment (val z: Int = n), arithmetic operands, comparison, function args, and return.
fun addOne(x: Int): Int = x + 1
fun firstOr(n: Int?, d: Int): Int { if (n != null) return n; return d }
fun main() {
    val n: Int? = 7
    if (n != null) {
        val z: Int = n
        println(z)              // 7
        println(z + 100)        // 107
        println(n + 1)          // 8
        println(addOne(n))      // 8
        println(n * 2)          // 14
        if (n > 5) println("gt5") else println("le5")
    }
    if (n != null && n > 5) println("big") else println("small")
    println(firstOr(7, -1))
    println(firstOr(null, -1))

    val l: Long? = 100L
    if (l != null) {
        println(l + 1L)         // 101
        println(l - 50L)        // 50
        if (l > 99L) println("lgt") else println("lle")
    }

    val d: Double? = 2.5
    if (d != null) {
        val w: Double = d
        println(w)              // 2.5
        println(w + 0.25)       // 2.75
        if (d < 3.0) println("dlt") else println("dge")
    }
}
