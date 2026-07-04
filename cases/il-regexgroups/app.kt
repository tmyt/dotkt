// Coverage (POLISH Wave-2 family 6, item 5): MatchResult.groups — the ClrMatchGroupCollection surface, which
// il-regex never touches. Pinned + fixed a bug: ClrMatchGroupCollection extended AbstractCollection<MatchGroup?>,
// whose abstract-generic base failed to type-load when `.groups` was first read (`Could not load type
// kotlin.collections.AbstractCollection`1`), and it had no direct `contains`, so a `group in match.groups` check
// had no member to dispatch. ClrMatchGroupCollection now implements MatchNamedGroupCollection DIRECTLY (spelling
// out contains/containsAll/isEmpty), so `.groups`, by-index/by-name access, iteration and `in` all work.
fun main() {
    val re = "(\\d+)-(\\d+)".toRegex()
    val m = re.find("12-34")!!
    val g = m.groups
    println(g.size)                             // 3 (whole match + 2 groups)
    println(g[0]?.value)                        // 12-34
    println(g[1]?.value)                        // 12
    println(g[2]?.value)                        // 34

    // iteration
    val vals = StringBuilder()
    for (grp in g) { vals.append(grp?.value ?: "?"); vals.append(",") }
    println(vals.toString())                    // 12-34,12,34,

    // `in` -> ClrMatchGroupCollection.contains
    val first = g.iterator().next()
    println(first in g)                          // true
    println(null in g)                           // false
    println(g.containsAll(listOf(g[0], g[1])))   // true

    // named group
    val named = "(?<yr>\\d{4})".toRegex().find("2026")!!
    println(named.groups["yr"]?.value)           // 2026
}
