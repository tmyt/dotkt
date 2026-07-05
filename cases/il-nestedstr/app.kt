// Regression: nested collection/map stringification (final-review N7). Only the TOP-LEVEL operand was routed to
// clrCollToString/clrMapToString at the static-type level; a NESTED collection/map element hit .NET's Object.ToString()
// and printed the raw `System.Collections.Generic.List`1[System.Int32]`. The fix routes every rendered element through
// the stdlib `clrElemToString`, which detects a nested collection/map at runtime (via non-generic BCL facades that
// dodge the value-type generic-variance InvalidCast) and recurses.
fun main() {
    println(mapOf("k" to listOf(1, 2)))              // {k=[1, 2]}
    println(listOf(listOf(1, 2)))                    // [[1, 2]]
    println(listOf(listOf(1, 2), listOf(3, 4)))      // [[1, 2], [3, 4]]
    println(mapOf("a" to mapOf("x" to 1)))           // {a={x=1}}
    println(listOf("s", "t"))                        // [s, t]   (String is not a nested collection)
    println(mapOf("k" to 5))                         // {k=5}    (scalar value)
}
