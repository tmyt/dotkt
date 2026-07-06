// C2 (kcc review): groupBy returns Map<K, List<V>> but builds a Dictionary<K, MutableList<V>> at runtime; CLR
// IDictionary<,> is INVARIANT in the value, so the runtime map is NOT assignable to the read interface. The whole
// READ surface (print / index / iterate / entries / keys / values) is therefore routed through the NON-GENERIC
// System.Collections.IDictionary (ClrMapDefaults) — every Dictionary<K,V> implements it regardless of V — so a read
// no longer throws EntryPointNotFound/InvalidCast. JVM-oracle differential: output must match real Kotlin/JVM
// (groupBy is a LinkedHashMap → insertion order).
//
// NOTE: `mapValues` / a direct `m.size`/`m.containsKey` on a groupBy result are exercised in il-mapvalues (#29):
// size/containsKey are UNBOUND on the Map interface and route (bir2cir Rule 5m) to the covariance-safe
// ClrMapDefaults.clrMapSize/clrMapContainsKey (non-generic ICollection.Count / IDictionary.Contains).
fun main() {
    // value-type key (Int) — the {it % 2} grouping; insertion order 1 (from 1) then 0 (from 2).
    val g = listOf(1, 2, 3, 4).groupBy { it % 2 }
    println(g)                                   // {1=[1, 3], 0=[2, 4]}
    println(g.keys)                              // [1, 0]
    println(g.values)                            // [[1, 3], [2, 4]]
    println(g[1])                                // [1, 3]
    println(g[0])                                // [2, 4]
    for ((k, v) in g) println("$k -> $v")        // 1 -> [1, 3] / 0 -> [2, 4]
    for (e in g.entries) println("${e.key}:${e.value}") // 1:[1, 3] / 0:[2, 4]

    // reference-type key (String) — groupBy by first char; read via index + iterate.
    val words = listOf("apple", "avocado", "banana", "cherry")
    val byFirst = words.groupBy { it.first().toString() }
    println(byFirst)                             // {a=[apple, avocado], b=[banana], c=[cherry]}
    println(byFirst["a"])                        // [apple, avocado]
    for ((k, v) in byFirst) println("$k=${v.size}")  // a=2 / b=1 / c=1
}
