// Sorting ops -> LINQ Order/OrderBy/OrderByDescending/OrderDescending (deferred; materialized by joinToString here).
fun main() {
    val ns = listOf(3, 1, 4, 1, 5, 9, 2, 6)
    println(ns.sortedDescending().joinToString(","))                 // 9,6,5,4,3,2,1,1

    val ws = listOf("bbb", "a", "cccc", "dd")
    println(ws.sortedBy { it.length }.joinToString(","))             // a,dd,bbb,cccc
    println(ws.sortedByDescending { it.length }.joinToString(","))   // cccc,bbb,dd,a
}
