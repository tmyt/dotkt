// C5: deterministic Kotlin hashCode() on String/Double/Float overrides the .NET randomized GetHashCode.
// The gate falls through to the declared stdlib body; primitive Int/Long toString/equals/hashCode stay
// on the correct BCL slot (the primitive fall-through guard).
fun main() {
    println("Aa".hashCode())
    println("".hashCode())
    println("hello".hashCode())
    println((-0.0).hashCode())
    println(0.0.hashCode())
    println((-0.0f).hashCode())
    println(5.toString())
    println(5.equals(5))
    println(5.hashCode())
    println((-7).hashCode())
    println(42.toString(16))
}
