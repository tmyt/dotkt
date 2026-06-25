// Mutable-collection instance members now bind to the BCL List<T> (add/remove/clear/removeAt), and a generic
// `ArrayList<R>()` builds a real List<R> by iterating + .add(...) — the shape the real stdlib map/filter/mapTo use.
fun <T, R> Iterable<T>.mapTo2(transform: (T) -> R): List<R> {
    val out = ArrayList<R>()          // -> new System.Collections.Generic.List<R>()
    for (item in this) out.add(transform(item))   // iterate Iterable over the BCL list; .add -> List.Add
    return out
}
fun main() {
    val m = mutableListOf(1, 2, 3)
    m.add(4)
    m.removeAt(0)
    println(m.joinToString(","))                  // 2,3,4
    m.remove(3)
    println(m.joinToString(","))                  // 2,4
    println(m.size)                               // 2
    m.clear()
    println(m.size)                               // 0
    println(listOf(1, 2, 3).mapTo2 { it * 11 }.joinToString(","))   // 11,22,33
}
