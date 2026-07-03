// bundle-6 ④ fix #6: a star-projected @ClrTypeAlias collection/map is-test lowers to the NON-generic BCL interface
// (ICollection/IList/IEnumerable/IDictionary), so it holds for a VALUE-type collection (reified generic isinst has no
// value-type covariance -> was silently false for List<int>).
fun main() {
    val c: Any = listOf(1, 2, 3)
    val m: Any = mapOf(1 to 2, 3 to 4)
    println(c is Collection<*>)         // true
    println(c is List<*>)               // true
    println(c is Iterable<*>)           // true
    println(m is Map<*, *>)             // true
    println(5 is Collection<*>)         // false
    println("hi" is List<*>)            // false
}
