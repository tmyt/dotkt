// Regression: nested collection/map inside Pair/Triple.toString (final-review C11). A tuple component's static type is
// the erased generic parameter, so `"($first, $second)"` used to stringify a nested runtime List/Map via .NET's raw
// `System.Collections.Generic.List`1[System.Int32]` Object.ToString(). Pair/Triple.toString now route each component
// through the runtime collection-aware stdlib stringifier (clrRenderTupleElement -> clrElemToString), matching Kotlin.
fun main() {
    println((listOf(1, 2) to listOf(3, 4)).toString())          // ([1, 2], [3, 4])
    println(Triple(listOf(1), listOf(2), listOf(3)).toString()) // ([1], [2], [3])
    println((mapOf(1 to 2) to listOf(3)).toString())            // ({1=2}, [3])
    println((1 to (2 to 3)).toString())                         // (1, (2, 3))
    println((listOf(listOf(1)) to 5).toString())                // ([[1]], 5)
    println((1 to 2).toString())                                // (1, 2)   (scalars unaffected)
    println((null to "a").toString())                           // (null, a)
}
