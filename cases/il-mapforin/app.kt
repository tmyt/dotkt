// for-in destructuring over a Map / MutableMap (Map.Entry component1/component2 + entries.iterator()).
// The MUTABLE map path was EntryPointNotFound (Map/MutableMap.iterator collide -> immutable overload
// binds -> the consumer must dispatch on Iterator<Map.Entry>, not Iterator<MutableEntry>).
fun main() {
    val im = mapOf("a" to 1, "b" to 2)
    for ((k, v) in im) println("$k=$v")            // a=1 / b=2

    val mm = mutableMapOf("c" to 3, "d" to 4)
    for ((k, v) in mm) println("$k=$v")            // c=3 / d=4

    var sum = 0
    for ((_, v) in mm) sum += v
    println(sum)                                    // 7

    for (e in mm.entries) println("${e.key}:${e.value}")   // c:3 / d:4
}
