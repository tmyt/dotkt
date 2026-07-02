// A6 regression gate — generic-token defects on CONCRETE generic alias-class receivers + map defaults.
//  (a) rule-3 member calls on a concrete generic alias receiver (HashMap<String,Int>().put/get/remove,
//      ArrayList<Int>().isEmpty/iterator, LinkedHashMap) must instantiate the <>dotkt_ClrH_* helper with the
//      receiver's class args (class-first, then method args — MergeTypeParams order) and carry the instantiated
//      receiver sig token (was: open-generic callStatic + bare `clr:` owner -> InvalidProgramException).
//  (b) getOrDefault on Map- AND MutableMap-typed receivers routes to the bare-V-returning ClrMapDefaults helper;
//      the call must carry `retType` so the result box uses the concrete instantiation (was: `box !!1` on the
//      callee's own method-generic token inside non-generic main() -> BadImageFormatException).
fun main() {
    // (a) concrete HashMap<String,Int>: rule-3 put/get/remove (previous-value semantics via the hoisted helper)
    val m = HashMap<String, Int>()
    m.put("a", 1)
    println(m.get("a") ?: -1)        // 1
    println(m.remove("a") ?: -1)     // 1
    println(m.remove("a") ?: -1)     // -1 (missing -> null -> elvis)

    // (b) getOrDefault: Map-typed receiver
    val ro: Map<String, Int> = mapOf("x" to 3, "y" to 4)
    println(ro.getOrDefault("x", 0)) // 3
    println(ro.getOrDefault("z", 9)) // 9
    // (b) getOrDefault: MutableMap-typed receiver
    val mm: MutableMap<String, Int> = HashMap()
    mm.put("b", 2)
    println(mm.getOrDefault("b", 0))    // 2
    println(mm.getOrDefault("nope", 7)) // 7

    // (a) concrete ArrayList<Int>: rule-3 isEmpty + iterator (for-loop over the concrete receiver)
    val l = ArrayList<Int>()
    println(if (l.isEmpty()) "empty" else "non-empty")   // empty
    l.add(10)
    l.add(20)
    l.add(30)
    l.removeAt(0)
    println(l[0])                    // 20
    var sum = 0
    for (x in l) sum += x
    println(sum)                     // 50

    // (a) concrete LinkedHashMap<String,Int> (same Dictionary alias, its own helper type)
    val lh = LinkedHashMap<String, Int>()
    lh.put("k", 5)
    println(lh.put("k", 6) ?: -1)    // 5
    println(lh.get("k") ?: -1)       // 6
    println(lh.remove("k") ?: -1)    // 6
}
