// Coverage (POLISH Wave-2 family 6, item 4): emptyMap() / mapOf() read-only-empty behavior. The reviewer's
// request to surface emptyMap() as System.Collections.Generic.IReadOnlyDictionary is architecturally BLOCKED
// (see docs/polish-review-layer-purity.md): kotlin.collections.Map @ClrTypeAlias-es to the MUTABLE IDictionary
// (deliberately NOT a read-only/mutable split — BCL IDictionary does not extend IReadOnlyDictionary, so a split
// breaks MutableMap:Map subtyping at the IL level), so emptyMap(): Map<K,V> must stay IDictionary-compatible;
// read-only-ness is Kotlin-frontend-enforced. CLR emptyMap() therefore returns a fresh Dictionary (LinkedHashMap),
// exactly as mapOf(pairs) does. This case LOCKS that read-only-empty behavior green so the coverage gap can't
// mask a regression.
fun main() {
    val e = emptyMap<String, Int>()
    println(e.size)                       // 0
    println(e.isEmpty())                  // true
    println(e["x"])                       // null
    println(e.containsKey("x"))           // false
    println(e.entries.size)               // 0

    val m = mapOf("a" to 1, "b" to 2)
    println(m.size)                       // 2
    println(m["a"])                       // 1
    println(m.isEmpty())                  // false

    val m0 = mapOf<String, Int>()         // empty mapOf() delegates to emptyMap()
    println(m0.size)                      // 0
    println(m0.isEmpty())                 // true
}
