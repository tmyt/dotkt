// More collection ops: partition, withIndex (destructured), associate, scan/runningFold, windowed, getOrElse.
fun main() {
    val xs = listOf(1, 2, 3, 4, 5, 6)
    val (even, odd) = xs.partition { it % 2 == 0 }
    println("${even.joinToString(",")} | ${odd.joinToString(",")}")          // 2,4,6 | 1,3,5

    for ((i, v) in listOf("a", "b", "c").withIndex()) print("$i:$v ")
    println()                                                                 // 0:a 1:b 2:c

    val m = listOf("x", "yy", "zzz").associate { it to it.length }
    println("${m["x"]},${m["yy"]},${m["zzz"]}")                               // 1,2,3

    println(listOf(1, 2, 3, 4).scan(0) { a, b -> a + b }.joinToString(","))   // 0,1,3,6,10
    println(listOf(1, 2, 3, 4).runningFold(100) { a, b -> a + b }.joinToString(",")) // 100,101,103,106,110
    println(listOf(1, 2, 3, 4, 5).windowed(3).map { it.sum() }.joinToString(",")) // 6,9,12

    println(xs.getOrElse(2) { -1 })                                          // 3
    println(xs.getOrElse(99) { it * -1 })                                    // -99
}
