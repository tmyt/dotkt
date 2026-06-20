// chunked (.NET Chunk + per-chunk ToList) and filterNotNull (Where x != null).
fun main() {
    val xs = listOf(1, 2, 3, 4, 5)
    println(xs.chunked(2).map { it.sum() }.joinToString(","))   // 3,7,5
    println(xs.chunked(2).size)                                 // 3
    println(xs.chunked(3).map { it.joinToString("-") }.joinToString(" "))  // 1-2-3 4-5

    val ns: List<String?> = listOf("a", null, "b", null, "c")
    println(ns.filterNotNull().joinToString(","))               // a,b,c
    println(ns.filterNotNull().size)                            // 3
}
