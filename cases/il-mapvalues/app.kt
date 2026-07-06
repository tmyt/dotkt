// #29 (kcc review): groupBy().mapValues{} + a direct `.size`/`.containsKey` on a groupBy result. mapValues reads the
// source map's `size` (mapCapacity pre-sizing) and its keys, and groupBy's runtime map is a value-type-mismatched
// Dictionary<K,IList<V>> read through IDictionary<K,IReadOnlyList<V>>. size/containsKey are UNBOUND on the Map
// interface and route (bir2cir Rule 5m) to the covariance-safe ClrMapDefaults.clrMapSize/clrMapContainsKey (non-generic
// ICollection.Count / IDictionary.Contains), so neither the transitive mapCapacity nor a direct read throws
// EntryPointNotFound. JVM-oracle differential: output must match real Kotlin/JVM (groupBy → insertion-ordered map).
fun main() {
    // The headline #29 repro: groupBy then mapValues over the grouped List values.
    val counts = listOf(1, 2, 3, 4).groupBy { it % 2 }.mapValues { it.value.size }
    println(counts)                          // {1=2, 0=2}

    // Direct size / containsKey on a groupBy result (value-type-mismatched map) — covariance-safe now.
    val g = listOf(1, 2, 3, 4, 5).groupBy { it % 2 }
    println(g.size)                          // 2
    println(g.containsKey(1))                // true
    println(g.containsKey(2))                // false
    println(g.mapValues { it.value.sum() })  // {1=9, 0=6}

    // reference-type key + mapValues over the grouped List size.
    val words = listOf("apple", "avocado", "banana", "cherry")
    val byFirst = words.groupBy { it.first() }.mapValues { it.value.size }
    println(byFirst)                         // {a=2, b=1, c=1}

    // A NORMAL (non-groupBy) map must keep size/containsKey correct after the unbind.
    val plain = mapOf("a" to 1, "b" to 2, "c" to 3)
    println(plain.size)                      // 3
    println(plain.containsKey("b"))          // true
    println(plain.containsKey("z"))          // false
    println(plain.mapValues { it.value * 10 })   // {a=10, b=20, c=30}
    val mm = mutableMapOf("x" to 1, "y" to 2)
    println(mm.size)                         // 2
    println(mm.containsKey("x"))             // true
}
