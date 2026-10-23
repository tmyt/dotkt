// LinkedHash* insertion-order family. Each old case's `main` + stdout-golden diff becomes
// one @TestAttribute method whose per-value assertEquals/assertTrue/assertFalse is strictly stronger (typed) than
// the old text diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-linkedorder -> linkedMapOrder  #169 LinkedHashMap/Set + mapOf/setOf insertion-order iteration, incl. AFTER a middle removal
//   il-linkedset   -> linkedSetBuild  #169 setOf/distinct/toMutableSet build the concrete LinkedHashSet (crash-free) + order contract
//
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

class LinkedCollectionTests {
    private fun assertMutableMapOrder(map: MutableMap<String, Int>) {
        map.clear()
        map["a"] = 1; map["b"] = 2; map["c"] = 3; map["d"] = 4
        map.remove("b"); map["e"] = 5
        assertEquals("a,c,d,e", map.keys.joinToString(","))
        map["c"] = 30; map["b"] = 20
        assertEquals("a,c,d,e,b", map.keys.joinToString(","))
        assertEquals("1,30,4,5,20", map.values.joinToString(","))
        assertEquals("a=1,c=30,d=4,e=5,b=20", map.entries.joinToString(",") { it.key + "=" + it.value })
    }

    private fun assertMutableSetOrder(set: MutableSet<String>) {
        set.clear()
        set.add("a"); set.add("b"); set.add("c"); set.add("d")
        set.remove("b"); set.add("e")
        assertEquals("a,c,d,e", set.joinToString(","))
        set.add("c"); set.add("b")
        assertEquals("a,c,d,e,b", set.joinToString(","))
    }

    @TestAttribute
    fun linkedMapOrder() {
        val m = LinkedHashMap<String, Int>()
        m["a"] = 1; m["b"] = 2; m["c"] = 3; m["d"] = 4
        m.remove("b")                                              // remove a MIDDLE key
        m["e"] = 5
        assertEquals("a,c,d,e", m.keys.joinToString(","))          // a,c,d,e
        assertEquals("a=1,c=3,d=4,e=5", m.entries.joinToString(",") { it.key + "=" + it.value }) // a=1,c=3,d=4,e=5
        assertEquals("1,3,4,5", m.values.joinToString(","))        // 1,3,4,5

        val s = LinkedHashSet<String>()
        s.add("x"); s.add("y"); s.add("z"); s.add("w")
        s.remove("y")                                              // remove a MIDDLE element
        s.add("q")
        assertEquals("x,z,w,q", s.joinToString(","))               // x,z,w,q
        assertEquals(4, s.size)                                    // 4
        assertTrue(s.contains("z"))                                // True
        assertFalse(s.contains("y"))                               // False

        // mapOf/setOf return LinkedHash*, so they are insertion-ordered too.
        assertEquals("one,two,three", mapOf("one" to 1, "two" to 2, "three" to 3).keys.joinToString(",")) // one,two,three
        assertEquals("p,d,b,a", setOf("p", "d", "b", "a").joinToString(","))  // p,d,b,a

        assertMutableMapOrder(mutableMapOf())
        assertMutableMapOrder(mutableMapOf("seed" to 0))
        assertMutableMapOrder(mutableMapOf("first" to 1, "second" to 2))
        val mixed = mapOf("first" to 1, "second" to "two")
        assertEquals(1, mixed["first"])
        assertEquals("two", mixed["second"])
    }

    @TestAttribute
    fun linkedSetBuild() {
        val d = listOf(3, 1, 2, 2, 4, 1).distinct()
        assertEquals("3,1,2,4", d.joinToString(","))     // 3,1,2,4
        assertEquals(4, d.size)                          // 4

        assertEquals(3, setOf(5, 5, 6, 7, 6).size)       // 3
        val ms = listOf("a", "b", "b", "c").toMutableSet()
        assertEquals("a,b,c", ms.joinToString(","))      // a,b,c

        val s = LinkedHashSet<String>()
        s.add("x"); s.add("y"); s.add("z"); s.add("w")
        s.remove("y")
        s.add("q")
        assertEquals("x,z,w,q", s.joinToString(","))     // x,z,w,q
        assertEquals(4, s.size)                          // 4
        assertTrue(s.contains("z"))                      // True
        assertFalse(s.contains("y"))                     // False

        val r = linkedSetOf(1, 2, 3, 4, 5)
        r.retainAll(setOf(2, 4, 5))
        assertEquals("2,4,5", r.joinToString(","))       // 2,4,5

        val g = linkedSetOf(10, 20, 30, 40)
        val gi = g.iterator()
        while (gi.hasNext()) { if (gi.next() == 20) gi.remove() }
        assertEquals("10,30,40", g.joinToString(","))    // 10,30,40

        assertMutableSetOrder(mutableSetOf())
        assertMutableSetOrder(mutableSetOf("seed"))
        assertMutableSetOrder(mutableSetOf("first", "second"))
        assertEquals("first,2,third", setOf("first", 2, "third").joinToString(","))
        val nullable = mutableSetOf<String?>("first", null, "third")
        assertTrue(nullable.contains(null))
        nullable.remove("first"); nullable.add("fourth")
        assertEquals("null,third,fourth", nullable.joinToString(","))
        nullable.remove(null); nullable.add(null)
        assertEquals("third,fourth,null", nullable.joinToString(","))
        assertEquals(1, setOf<String?>(null, null).size)
    }
}
