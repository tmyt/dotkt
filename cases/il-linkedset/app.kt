// #169 regression: setOf / distinct() / toMutableSet() build the CONCRETE LinkedHashSet class (backed by an
// insertion-ordered LinkedHashMap). Before the fix this crashed with InvalidProgramException — `new LinkedHashSet(coll)`
// (toMutableSet) resolved to the arity-collision `(Int)` ctor, and the class's OWN `iterator()` self-call (retainAll)
// was rerouted to the base-Iterator bridge / the ICollection Contains slot referenced the open generic self. This case
// locks BOTH the crash-free build AND the #169 insertion-order contract (incl. after a MIDDLE removal).
fun main() {
    // distinct() -> toMutableSet() -> LinkedHashSet(Collection<E>) ctor (the crash site), order-preserving.
    val d = listOf(3, 1, 2, 2, 4, 1).distinct()
    println(d.joinToString(","))            // 3,1,2,4
    println(d.size)                         // 4

    // setOf(vararg) -> toSet(); toMutableSet() explicit — both return an insertion-ordered LinkedHash* set.
    println(setOf(5, 5, 6, 7, 6).size)      // 3
    val ms = listOf("a", "b", "b", "c").toMutableSet()
    println(ms.joinToString(","))           // a,b,c

    // LinkedHashSet keeps insertion order across a MIDDLE removal + a re-add (the #169 contract).
    val s = LinkedHashSet<String>()
    s.add("x"); s.add("y"); s.add("z"); s.add("w")
    s.remove("y")
    s.add("q")
    println(s.joinToString(","))            // x,z,w,q
    println(s.size)                         // 4
    println(s.contains("z"))                // true
    println(s.contains("y"))                // false

    // retainAll walks the class's OWN iterator() (the reroute-suppression path), order-preserving.
    val r = linkedSetOf(1, 2, 3, 4, 5)
    r.retainAll(setOf(2, 4, 5))
    println(r.joinToString(","))            // 2,4,5

    // APP-side DIRECT iterator() + it.remove() on a LinkedHashSet: the reroute must bind the class's own
    // MutableIterator (its remove()), NOT the base-Iterator bridge (EntryPointNotFound on remove() otherwise).
    val g = linkedSetOf(10, 20, 30, 40)
    val gi = g.iterator()
    while (gi.hasNext()) { if (gi.next() == 20) gi.remove() }
    println(g.joinToString(","))            // 10,30,40
}
