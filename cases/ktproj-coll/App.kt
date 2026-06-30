// A PRACTICAL collections app that consumes the real CLR stdlib (DotKt.Stdlib.dll) from an MSBuild .ktproj.
// It exercises the "app consumes the rt stdlib" path: a `List` held as a local (resolves as the referenced
// IReadOnlyList), member access (size / indexing), and — the point of this sample — TOP-LEVEL stdlib functions
// (first / getOrElse / contains / indexOf / count / isEmpty / take), which kotc emits as `callStatic owner=null`
// and bir2cir attributes to the file-class they live in (kotlin.collections._CollectionsKt) so ilemit resolves
// them against the runtime stdlib. Builds and runs via `dotnet build` + `dotnet run`.
fun main() {
    val nums = listOf(10, 20, 30, 40, 50)
    println(nums.size)                  // 5
    println(nums[2])                    // 30
    println(nums.first())               // 10   (top-level fun -> _CollectionsKt.first)
    println(nums.getOrElse(1) { -1 })   // 20   (top-level fun, in range)
    println(nums.getOrElse(10) { -1 })  // -1   (out of range -> default lambda)
    println(nums.contains(30))          // True
    println(nums.indexOf(40))           // 3
    println(nums.count())               // 5
    println(nums.isEmpty())             // False
    println(nums.take(2).size)          // 2

    val words = listOf("apple", "pear", "fig")
    println(words.first().uppercase())  // APPLE
    println(words[1])                   // pear
}
