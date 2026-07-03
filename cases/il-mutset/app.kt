// bundle-6 ④ fix #3: MutableList.set / removeAt RETURN the previous/removed element in Kotlin but bind to VOID BCL
// slots — routed to clrListSet/clrListRemoveAt so the returned value is available (no InvalidProgramException).
fun main() {
    val l = mutableListOf(10, 20, 30)
    val old = l.set(1, 99)
    println(old)                        // 20
    println(l.joinToString(","))        // 10,99,30
    val rm = l.removeAt(0)
    println(rm)                         // 10
    println(l.joinToString(","))        // 99,30
}
