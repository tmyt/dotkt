// Iterating a non-literal IntRange obtained from `.indices` (Collection and CharSequence) — counter-lowered
// so it does not hit the IntIterator protocol.
fun main() {
    for (i in listOf("a", "b", "c").indices) print(i)
    println()
    for (i in "hello".indices) print(i)
    println()
    val r = listOf("x", "y", "z", "w").indices
    for (i in r) print(i)
    println()
    for (i in listOf<String>().indices) print(i)
    println("end")
}
