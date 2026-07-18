// #169: LinkedHashMap / LinkedHashSet (and mapOf/setOf) CONTRACT insertion-order iteration, including AFTER a removal.
fun main() {
    val m = LinkedHashMap<String, Int>()
    m["a"] = 1; m["b"] = 2; m["c"] = 3; m["d"] = 4
    m.remove("b")                                            // remove a MIDDLE key
    m["e"] = 5
    println(m.keys.joinToString(","))                        // a,c,d,e
    println(m.entries.joinToString(",") { it.key + "=" + it.value }) // a=1,c=3,d=4,e=5
    println(m.values.joinToString(","))                     // 1,3,4,5

    val s = LinkedHashSet<String>()
    s.add("x"); s.add("y"); s.add("z"); s.add("w")
    s.remove("y")                                            // remove a MIDDLE element
    s.add("q")
    println(s.joinToString(","))                            // x,z,w,q
    println(s.size)                                          // 4
    println(s.contains("z"))                                // True
    println(s.contains("y"))                                // False

    // mapOf/setOf return LinkedHash*, so they are insertion-ordered too.
    println(mapOf("one" to 1, "two" to 2, "three" to 3).keys.joinToString(",")) // one,two,three
    println(setOf("p", "d", "b", "a").joinToString(","))    // p,d,b,a
}
