// §5a (kcc review C14 follow-up, 2026-07-06): an EXPLICIT `.equals()` call now follows Kotlin's TOTAL
// order (boxed Double/Float) / STRUCTURAL equality (collections), like the `==` operator — not
// Object.Equals (IEEE `-0.0 == 0.0` / reference identity). A plain-object `.equals()` stays reference
// identity. JVM-oracle PURE.
fun main() {
    println((-0.0).equals(0.0))                  // false — total order (-0.0 != 0.0)
    println(0.0.equals(0.0))                     // true
    println(Double.NaN.equals(Double.NaN))       // true  — NaN == NaN structurally
    println((-0.0f).equals(0.0f))                // false — Float total order
    println(1.5.equals(1.5))                     // true
    println(listOf(1, 2).equals(listOf(1, 2)))   // true  — List structural (ordered)
    println(listOf(1, 2).equals(listOf(2, 1)))   // false
    println(setOf(1, 2).equals(setOf(2, 1)))     // true  — Set structural (unordered)
    println(mapOf(1 to 2).equals(mapOf(1 to 2))) // true  — Map structural (entrywise)
    val a: Any = Any(); val b: Any = Any()
    println(a.equals(b))                         // false — plain object reference identity
    println(a.equals(a))                         // true
    println("hi".equals("hi"))                   // true  — String value equality (its own binding)
}
