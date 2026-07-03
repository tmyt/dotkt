// bundle-6 ④ fix #4: the JVM (initialCapacity, loadFactor) collection ctor has no BCL (int,float) equivalent — the
// loadFactor arg is dropped so it resolves to the capacity-only (int) ctor instead of mis-picking the IEnumerable one.
fun main() {
    val s = HashSet<Int>(16, 0.75f)
    s.add(1); s.add(2); s.add(2)
    println(s.size)                     // 2
    val ls = LinkedHashSet<String>(8, 0.5f)
    ls.add("a"); ls.add("b")
    println(ls.size)                    // 2
    val m = HashMap<Int, String>(8, 0.5f)
    m[1] = "x"
    println(m.size)                     // 1
    val lm = LinkedHashMap<Int, Int>(4, 0.9f)
    lm[1] = 10
    println(lm.size)                    // 1
}
