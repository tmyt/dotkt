// chunked (.NET Chunk + per-chunk ToList) and filterNotNull (Where x != null).
fun main() {
    val xs = listOf(1, 2, 3, 4, 5)
    println(xs.chunked(2).map { it.sum() }.joinToString(","))   // 3,7,5
    println(xs.chunked(2).size)                                 // 3
    println(xs.chunked(3).map { it.joinToString("-") }.joinToString(" "))  // 1-2-3 4-5

    val ns: List<String?> = listOf("a", null, "b", null, "c")
    println(ns.filterNotNull().joinToString(","))               // a,b,c  (reference T?)
    println(ns.filterNotNull().size)                            // 3

    val vs: List<Int?> = listOf(1, null, 3, null, 5)
    println(vs.filterNotNull().joinToString(","))               // 1,3,5  (value-type Nullable<Int> unwrap)
    println(vs.filterNotNull().sum())                           // 9
}
