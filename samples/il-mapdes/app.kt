// Spread `*array` into a vararg, and destructuring a Map in a for-loop (`for ((k,v) in map)`).
fun sum(vararg xs: Int): Int { var s = 0; for (x in xs) s += x; return s }

fun main() {
    // spread an existing array into a vararg parameter
    val a = intArrayOf(1, 2, 3, 4)
    println(sum(*a))                               // 10
    println(sum(10, 20, 30))                       // 60  (plain literal vararg still works)
    println(sum(1, *a, 2))                         // 13  (mixed: literals + spread)

    // destructure Map.Entry in a for-loop -> Dictionary enumeration yielding KeyValuePair (.Key/.Value)
    val m = mapOf("x" to 1, "y" to 2, "z" to 3)
    var total = 0
    for ((k, v) in m) { println("$k=$v"); total += v }
    println("total=$total")                        // 6
}
