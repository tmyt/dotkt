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
    val notColl: Any = 5                // Any-typed so the is-check is a runtime test, not a
    val notList: Any = "hi"             // statically-false one (Kotlin 2.4.0 errors on `5 is Collection<*>`)
    println(notColl is Collection<*>)   // false
    println(notList is List<*>)         // false
}
