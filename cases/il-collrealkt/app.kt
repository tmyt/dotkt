// Real-Kotlin generic collection extensions exercising indexer/size member access on List<T>/MutableList<T>/Map<K,V>
// — these resolve through ilemit's GenericMethod (TypeBuilder.GetMethod) routing, since e.g. List<T> with a method
// type parameter T is a TypeBuilderInstantiation whose plain reflection .GetMethod throws. The first step of moving
// random-access collection ops off the hand-written LINQ lowering onto real Kotlin source running on the BCL.
fun <T> List<T>.firstE(): T {
    if (size == 0) throw NoSuchElementException("List is empty.")
    return this[0]
}
fun <T> List<T>.lastE(): T = this[size - 1]
fun <T> List<T>.getOrElseE(index: Int, defaultValue: (Int) -> T): T =
    if (index in 0 until size) this[index] else defaultValue(index)
fun <T> MutableList<T>.swap01() { val t = this[0]; this[0] = this[1]; this[1] = t }
fun <K, V> Map<K, V>.valAt(k: K): V = this[k]!!

fun main() {
    val xs = listOf(10, 20, 30)
    println(xs.firstE())                      // 10
    println(xs.lastE())                       // 30
    println(xs.getOrElseE(5) { it * 100 })    // 500
    val m = mutableListOf("a", "b", "c")
    m.swap01()
    println(m.joinToString(","))              // b,a,c
    val d = mapOf(1 to "one", 2 to "two")
    println(d.valAt(2))                        // two
}
