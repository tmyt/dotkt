// Value-type (Int) lazy sequence with `.filter{}` (FilteringSequence): the iterator's nextItem:T? field + its
// get_/set_ accessors erase to object, and calcNext's set_nextItem(item) boxes the value element (bundle-6 BUG-1).
fun main() {
    val xs = listOf(1, 2, 3, 4, 5, 6)
    // filter over a value-type sequence, materialized by toList.
    println(xs.asSequence().filter { it > 2 }.toList().joinToString(","))   // 3,4,5,6
    // filter then map (chained value-type FilteringSequence + TransformingSequence).
    println(xs.asSequence().filter { it % 2 == 0 }.map { it * 10 }.toList().joinToString(","))  // 20,40,60
    // filter with a terminal first (short-circuit).
    println(xs.asSequence().filter { it > 3 }.first())   // 4
    // filterNot (sendWhen=false path).
    println(xs.asSequence().filterNot { it < 3 }.toList().joinToString(","))  // 3,4,5,6
    // count over a value-type filter.
    println(xs.asSequence().filter { it % 2 == 1 }.count())  // 3
}
