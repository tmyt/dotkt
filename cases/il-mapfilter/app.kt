// map/filter MIGRATED off the COLLECTION_OPS LINQ lowering: a collection receiver routes to the real Kotlin body in
// DotKt.Stdlib (iterate + build an ArrayList), while Array/Sequence receivers keep the LINQ lowering (DotKt.Stdlib
// ships only the Iterable overload). Both must produce identical results.
fun main() {
    val xs = listOf(1, 2, 3, 4, 5)
    println(xs.map { it * 10 }.joinToString(","))             // collection map -> real Kotlin: 10,20,30,40,50
    println(xs.filter { it % 2 == 0 }.joinToString(","))      // collection filter -> real Kotlin: 2,4
    println(xs.map { it + 1 }.filter { it > 3 }.joinToString(","))  // chained: 4,5,6
    println(arrayOf(1, 2, 3).map { it * 100 }.joinToString(",")) // array map -> LINQ: 100,200,300
    println(setOf(1, 2, 2, 3).map { it * 2 }.joinToString(","))  // set (Iterable->IEnumerable) map -> real Kotlin: 2,4,6
}
