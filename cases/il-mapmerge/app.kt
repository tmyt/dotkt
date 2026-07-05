// Regression: MutableMap.merge (final-review C2). On Kotlin/JVM `merge` is java.util.Map.merge (a member taking a
// java.util.function.BiFunction); the CLR MutableMap has no such mapping, so the lambda materialized as `Func<V,V,object>`
// was `castclass`-ed to the `? super V`-erased `Func<object,object,object>` -> InvalidCastException. The stdlib now
// declares `merge` on the MutableMap builtin with a Kotlin function type (the frontend binds to THIS overload, not the
// SAM one) and routes a BCL-aliased receiver to ClrMapDefaults.clrMapMerge. Absent key inserts value; present key applies
// the remapping function; a null result removes the entry. Semantics mirror java.util.Map.merge.
fun main() {
    val m = mutableMapOf(1 to 10)
    println(m.merge(1, 5) { a, b -> a + b })   // 15 (present -> 10+5)
    println(m.merge(2, 7) { a, b -> a + b })   // 7  (absent  -> insert)
    println(m[1])                              // 15
    println(m[2])                              // 7
    println(m.merge(1, 0) { _, _ -> null })    // null (remove)
    println(m.containsKey(1))                  // false
    val s = mutableMapOf("x" to "a")
    println(s.merge("x", "b") { o, n -> o + n })   // ab
    println(s.merge("y", "z") { o, n -> o + n })   // z
}
