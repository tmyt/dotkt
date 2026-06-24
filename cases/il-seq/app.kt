// Kotlin `Sequence` (lazy) -> a deferred .NET IEnumerable<T>. `asSequence()` is a pass-through (LINQ is already
// lazy); intermediate ops (map/filter/take) stay deferred and only a terminal (toList/first/count/sum) forces.
fun main() {
    val xs = listOf(1, 2, 3, 4, 5, 6)

    // Lazy chain: map -> filter, materialized by toList.
    val r = xs.asSequence()
        .map { it * 2 }            // 2,4,6,8,10,12
        .filter { it % 3 == 0 }    // 6,12
        .toList()
    println(r.joinToString(","))   // 6,12

    // Terminal `first` short-circuits a lazy chain.
    val firstBig = xs.asSequence().map { it * it }.filter { it > 10 }.first()   // 16
    println(firstBig)

    // `count` / `sum` as terminals over a lazy sequence.
    println(xs.asSequence().filter { it % 2 == 0 }.count())   // 3
    println(xs.asSequence().map { it + 1 }.sum())             // 2+3+4+5+6+7 = 27

    // `take` (deferred) then materialize.
    println(xs.asSequence().map { it * 10 }.take(3).toList().joinToString("-"))   // 10-20-30

    // `takeWhile` / `dropWhile` (deferred) and `single` (terminal).
    println(xs.asSequence().takeWhile { it < 4 }.toList().joinToString(","))      // 1,2,3
    println(xs.asSequence().dropWhile { it < 4 }.toList().joinToString(","))      // 4,5,6
    println(xs.asSequence().single { it == 3 })                                   // 3
}
