// #30 (kcc review): a VALUE-element collection's `.indices` / `.lastIndex`. `Collection<*>.indices` used a star
// projection that lowered its receiver to the reified IReadOnlyCollection<object>; a value-element runtime list
// (ArrayList<int> : IReadOnlyCollection<int>) does NOT implement it (CLR generic covariance excludes value-type
// args), so reading `size` (get_Count) threw EntryPointNotFound. Genericizing to `Collection<T>.indices` keeps the
// receiver IReadOnlyCollection<T>, covariance-safe for value elements (the same shape as `List<T>.lastIndex`, which
// already worked). Reference-element collections (covariance holds for reference args) must stay green.
// JVM-oracle differential: output must match real Kotlin/JVM.
fun main() {
    // value-element (Int) — the #30 crash site.
    for (i in listOf(1, 2, 3).indices) print(i)
    println()                                     // 012
    println(listOf(1, 2, 3).lastIndex)            // 2
    println(listOf(10, 20).lastIndex)             // 1
    println(listOf<Int>().lastIndex)              // -1
    for (i in listOf<Int>().indices) print(i)
    println("e")                                  // e (empty indices → empty range)

    // Double element (value type).
    val d = listOf(1.5, 2.5, 3.5)
    var s = 0
    for (i in d.indices) s += i
    println(s)                                    // 3 (0+1+2)

    // reference-element (String) — covariance held before, must still hold.
    for (i in listOf("a", "b", "c").indices) print(i)
    println()                                     // 012
    println(listOf("x", "y", "z", "w").lastIndex) // 3
}
