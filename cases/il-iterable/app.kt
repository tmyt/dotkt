// User class implementing Kotlin's Iterator<T> / Iterable<T>. IL can't define a generic interface here, so each is
// mapped to a monomorphized synthetic interface (`<>dotkt_KIterator_<elem>` / `<>dotkt_KIterable_<elem>`). Both the
// `for` loop (generic iterator protocol) and explicit `.iterator()` + hasNext/next dispatch through them.
class Countdown(var n: Int) : Iterator<Int> {
    override fun hasNext(): Boolean = n > 0
    override fun next(): Int { val r = n; n -= 1; return r }
}
class Range3 : Iterable<Int> {
    override fun iterator(): Iterator<Int> = Countdown(3)
}
fun main() {
    for (x in Range3()) print(x)           // 321
    println()
    val it = Range3().iterator()           // explicit first-class Kotlin Iterator
    var s = 0
    while (it.hasNext()) s += it.next()
    println(s)                              // 6
    var c = 0
    for (x in Range3()) c += x             // for-loop again (fresh iterator)
    println(c)                              // 6
}
