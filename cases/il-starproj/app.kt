// #60: a value-type-arg collection (Dictionary<int,int> / List<int>) erased to `Any` and smart-cast to a
// star-projection (`Map<*,*>` / `List<*>` / `Iterable<*>` / `Collection<*>`) must lower the resulting CAST to the
// NON-GENERIC BCL interface (System.Collections.IDictionary/IList/...). CLR reified generics are INVARIANT, so a
// `Dictionary<int,int>` is NOT an `IDictionary<object,object>` -> a castclass to the object-erased generic interface
// throws InvalidCastException (the JVM erases both to `Map`, hiding it). `println` of such an erased value routes to
// `clrElemToString`, which detects the collection at runtime via the non-generic facades and renders Kotlin-style;
// `.size` re-points onto the non-generic `ICollection.Count`; `[i]` onto the non-generic `IList.get_Item`.
fun main() {
    val g: Any = hashMapOf(1 to 2, 3 to 4)
    if (g is Map<*, *>) {
        println(g)                         // {1=2, 3=4}
        println(g.size)                    // 2
    }
    val l: Any = listOf(10, 20, 30)
    if (l is List<*>) {
        println(l)                         // [10, 20, 30]
        println(l.size)                    // 3
        println(l[1])                      // 20
    }
    if (l is Iterable<*>) println(l)       // [10, 20, 30]
    if (l is Collection<*>) println(l)     // [10, 20, 30]

    // Explicit as-cast escape hatch flowing straight into println (was InvalidCast at the castclass).
    @Suppress("UNCHECKED_CAST")
    println(g as Map<Any?, Any?>)          // {1=2, 3=4}

    // Negatives: a non-collection is not a Map/List.
    println((5 as Any) is Map<*, *>)       // False
    println(("x" as Any) is List<*>)       // False
}
