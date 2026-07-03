// A Map operand of println prints Kotlin-style `{a=1, b=2}` (AbstractMap.toString), NOT the raw .NET
// `System.Collections.Generic.Dictionary`2[...]`. Routed at the STATIC type level in kotc to clrMapToString,
// mirroring the List path (clrCollToString) — a runtime `is Map<*,*>` is unreliable for @ClrTypeAlias-lowered
// BCL dictionaries. (Single-pair `mapOf(pair)` is avoided: its stdlib actual currently yields an empty map,
// an orthogonal stdlib bug; the multi-arg/mutable forms exercise the routing.)
fun main() {
    println(mapOf("a" to 1, "b" to 2))
    val mm = mutableMapOf<String, Int>()
    mm["x"] = 9
    println(mm)
    println(listOf(1, 2, 3))
}
