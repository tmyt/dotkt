// #167/#168: String/Float/Double hashCode() binds to CLR-native GetHashCode (no hand-rolled JVM
// polynomial / toBits body). The Kotlin contract requires only within-run consistency + equals-
// consistency, NOT a specific integer value or across-run determinism, so this case asserts
// BEHAVIOR (equal values hash equal, hash-set membership) — never a pinned hash integer. Primitive
// Int stays on the correct BCL slot (Int32.GetHashCode returns the value itself; toString/equals).
fun main() {
    // String: within-run consistency + equal strings hash equal + hash-set membership.
    println("Aa".hashCode() == "Aa".hashCode())
    println("hello".hashCode() == ("hel" + "lo").hashCode())
    println(hashSetOf("a", "b", "c").contains("b"))
    // Double/Float: NaN canonicalized (equal -> equal hash) + set membership (the legal -0.0/+0.0
    // hash collision compares unequal, which HashSet resolves).
    println(Double.NaN.hashCode() == Double.NaN.hashCode())
    println(hashSetOf(1.5, 2.5).contains(1.5))
    println((-0.0f).hashCode() == (-0.0f).hashCode())
    // Primitive Int/Long stay on the BCL slot (unchanged by #167/#168).
    println(5.toString())
    println(5.equals(5))
    println(5.hashCode())
    println((-7).hashCode())
    println(42.toString(16))
}
