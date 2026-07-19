// Maps battery — migrates the Map-typed family of cases/il-* (mapOf / groupBy / merge / mapValues / eachCount /
// map-destructure / getOrDefault / Map toString / for-in over a Map) onto the in-process NUnit suite. Each old
// case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assert is strictly stronger
// (typed, fails the exact broken contract). Every value the old il_check (or, for the two PURE-only cases
// il-groupby2 / il-mapmerge's differential claim, the JVM oracle) asserted is preserved 1:1 (see `// <expected>`).
// Interior loop `println`s that were part of a case's contract are captured into a list and asserted in order.
//
// The list / set / iteration / collection-operation family lives in the sibling CollectionsTests battery.
//
// Coverage preserved (old case -> method):
//   il-eachcount -> eachcount_grouping   Grouping.eachCount() (value-type-nullable smart-cast in arithmetic)
//   il-emptymap  -> emptymap_readEmpty   emptyMap()/mapOf() read-only-empty (size/isEmpty/index/containsKey/entries)
//   il-groupby2  -> groupby2_readSurface groupBy read surface (print/index/iterate/entries/keys/values), JVM-oracle
//   il-mapdes    -> mapdes_spreadDestr   vararg spread (*array) + Map.Entry for-in destructuring
//   il-mapforin  -> mapforin_forIn       for-in destructuring over Map / MutableMap (component1/component2)
//   il-mapgen    -> mapgen_concreteGen   concrete generic HashMap/ArrayList/LinkedHashMap rule-3 + getOrDefault
//   il-mapmerge  -> mapmerge_merge       MutableMap.merge (insert / remap / null-removes), JVM-oracle
//   il-mapof1    -> mapof1_singlePair    mapOf single-pair overload (since-1.9) + mutable parity
//   il-maptostr  -> maptostr_toString    Map operand prints Kotlin-style {a=1, b=2}, not the raw Dictionary`2
//   il-mapvalues -> mapvalues_mapValues  groupBy().mapValues{} + direct size/containsKey on a groupBy result (#29)
//
// Top-level names are unique within this single battery assembly (one project = one namespace); il-mapdes's `sum`
// vararg helper is renamed varargSum to avoid shadowing the stdlib Iterable.sum extension.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

// ---- il-mapdes : spread `*array` into a vararg ----------------------------------------------------------------
fun varargSum(vararg xs: Int): Int { var s = 0; for (x in xs) s += x; return s }

class MapsTests {
    @TestAttribute
    fun eachcount_grouping() {
        // Grouping.eachCount() reads a value-type-nullable smart-cast (Int?) in arithmetic (C1 value-slot-unwrap).
        assertEquals("{a=2, b=1}", listOf("a", "ab", "b").groupingBy { it.first() }.eachCount().toString())   // {a=2, b=1}
        assertEquals("{M=1, i=4, s=4, p=2}", "Mississippi".groupingBy { it }.eachCount().toString())          // {M=1, i=4, s=4, p=2}
        assertEquals("{1=2, 2=2, 0=2}", listOf(1, 2, 3, 4, 5, 6).groupingBy { it % 3 }.eachCount().toString()) // {1=2, 2=2, 0=2}
    }

    @TestAttribute
    fun emptymap_readEmpty() {
        val e = emptyMap<String, Int>()
        assertEquals(0, e.size)                         // 0
        assertTrue(e.isEmpty())                         // True
        assertNull(e["x"])                              // null
        assertFalse(e.containsKey("x"))                 // False
        assertEquals(0, e.entries.size)                 // 0

        val m = mapOf("a" to 1, "b" to 2)
        assertEquals(2, m.size)                         // 2
        assertEquals(1, m["a"])                         // 1
        assertFalse(m.isEmpty())                        // False

        val m0 = mapOf<String, Int>()                   // empty mapOf() delegates to emptyMap()
        assertEquals(0, m0.size)                        // 0
        assertTrue(m0.isEmpty())                        // True
    }

    @TestAttribute
    fun groupby2_readSurface() {
        // groupBy's runtime Dictionary<K,MutableList<V>> read through the NON-GENERIC IDictionary; JVM-oracle order.
        val log = mutableListOf<String>()
        val g = listOf(1, 2, 3, 4).groupBy { it % 2 }
        log.add("$g")                                   // {1=[1, 3], 0=[2, 4]}
        log.add("${g.keys}")                            // [1, 0]
        log.add("${g.values}")                          // [[1, 3], [2, 4]]
        log.add("${g[1]}")                              // [1, 3]
        log.add("${g[0]}")                              // [2, 4]
        for ((k, v) in g) log.add("$k -> $v")           // 1 -> [1, 3] / 0 -> [2, 4]
        for (e in g.entries) log.add("${e.key}:${e.value}") // 1:[1, 3] / 0:[2, 4]

        val words = listOf("apple", "avocado", "banana", "cherry")
        val byFirst = words.groupBy { it.first().toString() }
        log.add("$byFirst")                             // {a=[apple, avocado], b=[banana], c=[cherry]}
        log.add("${byFirst["a"]}")                      // [apple, avocado]
        for ((k, v) in byFirst) log.add("$k=${v.size}") // a=2 / b=1 / c=1

        val expected = listOf(
            "{1=[1, 3], 0=[2, 4]}", "[1, 0]", "[[1, 3], [2, 4]]", "[1, 3]", "[2, 4]",
            "1 -> [1, 3]", "0 -> [2, 4]", "1:[1, 3]", "0:[2, 4]",
            "{a=[apple, avocado], b=[banana], c=[cherry]}", "[apple, avocado]", "a=2", "b=1", "c=1"
        ).joinToString("|")
        assertEquals(expected, log.joinToString("|"))
    }

    @TestAttribute
    fun mapdes_spreadDestr() {
        val a = intArrayOf(1, 2, 3, 4)
        assertEquals(10, varargSum(*a))                 // 10  (spread an array into a vararg)
        assertEquals(60, varargSum(10, 20, 30))         // 60  (plain literal vararg)
        assertEquals(13, varargSum(1, *a, 2))           // 13  (mixed: literals + spread)

        // destructure Map.Entry in a for-loop -> Dictionary enumeration yielding KeyValuePair (.Key/.Value)
        val m = mapOf("x" to 1, "y" to 2, "z" to 3)
        val log = mutableListOf<String>()
        var total = 0
        for ((k, v) in m) { log.add("$k=$v"); total += v }
        assertEquals("x=1|y=2|z=3", log.joinToString("|"))  // x=1 / y=2 / z=3
        assertEquals(6, total)                          // total=6
    }

    @TestAttribute
    fun mapforin_forIn() {
        // for-in destructuring over a Map / MutableMap (Map.Entry component1/component2 + entries.iterator()).
        val im = mapOf("a" to 1, "b" to 2)
        val log1 = mutableListOf<String>()
        for ((k, v) in im) log1.add("$k=$v")            // a=1 / b=2

        val mm = mutableMapOf("c" to 3, "d" to 4)
        for ((k, v) in mm) log1.add("$k=$v")            // c=3 / d=4
        assertEquals("a=1|b=2|c=3|d=4", log1.joinToString("|"))

        var sum = 0
        for ((_, v) in mm) sum += v
        assertEquals(7, sum)                            // 7

        val log2 = mutableListOf<String>()
        for (e in mm.entries) log2.add("${e.key}:${e.value}") // c:3 / d:4
        assertEquals("c:3|d:4", log2.joinToString("|"))
    }

    @TestAttribute
    fun mapgen_concreteGen() {
        // (a) concrete HashMap<String,Int>: rule-3 put/get/remove (previous-value semantics via the hoisted helper)
        val m = HashMap<String, Int>()
        m.put("a", 1)
        assertEquals(1, m.get("a") ?: -1)               // 1
        assertEquals(1, m.remove("a") ?: -1)            // 1
        assertEquals(-1, m.remove("a") ?: -1)           // -1 (missing -> null -> elvis)

        // (b) getOrDefault: Map-typed receiver
        val ro: Map<String, Int> = mapOf("x" to 3, "y" to 4)
        assertEquals(3, ro.getOrDefault("x", 0))        // 3
        assertEquals(9, ro.getOrDefault("z", 9))        // 9
        // (b) getOrDefault: MutableMap-typed receiver
        val mm: MutableMap<String, Int> = HashMap()
        mm.put("b", 2)
        assertEquals(2, mm.getOrDefault("b", 0))        // 2
        assertEquals(7, mm.getOrDefault("nope", 7))     // 7

        // (a) concrete ArrayList<Int>: rule-3 isEmpty + iterator (for-loop over the concrete receiver)
        val l = ArrayList<Int>()
        assertEquals("empty", if (l.isEmpty()) "empty" else "non-empty")   // empty
        l.add(10)
        l.add(20)
        l.add(30)
        l.removeAt(0)
        assertEquals(20, l[0])                          // 20
        var sum = 0
        for (x in l) sum += x
        assertEquals(50, sum)                           // 50

        // (a) concrete LinkedHashMap<String,Int>
        val lh = LinkedHashMap<String, Int>()
        lh.put("k", 5)
        assertEquals(5, lh.put("k", 6) ?: -1)           // 5
        assertEquals(6, lh.get("k") ?: -1)              // 6
        assertEquals(6, lh.remove("k") ?: -1)           // 6
    }

    @TestAttribute
    fun mapmerge_merge() {
        // MutableMap.merge: absent key inserts value; present key applies remap; a null result removes the entry.
        val m = mutableMapOf(1 to 10)
        assertEquals(15, m.merge(1, 5) { a, b -> a + b })   // 15 (present -> 10+5)
        assertEquals(7, m.merge(2, 7) { a, b -> a + b })    // 7  (absent  -> insert)
        assertEquals(15, m[1])                              // 15
        assertEquals(7, m[2])                               // 7
        assertNull(m.merge(1, 0) { _, _ -> null })          // null (remove)
        assertFalse(m.containsKey(1))                       // False
        val s = mutableMapOf("x" to "a")
        assertEquals("ab", s.merge("x", "b") { o, n -> o + n }) // ab
        assertEquals("z", s.merge("y", "z") { o, n -> o + n })  // z
    }

    @TestAttribute
    fun mapof1_singlePair() {
        val m = mapOf("a" to 1)                         // single-pair overload (since-1.9)
        assertEquals(1, m.size)                         // 1
        assertEquals(1, m["a"])                         // 1
        assertEquals(2, mapOf("x" to 1, "y" to 2).size) // 2 (vararg still works)
        assertEquals(1, mutableMapOf("k" to 7).size)    // 1 (mutable parity)
    }

    @TestAttribute
    fun maptostr_toString() {
        // A Map operand prints Kotlin-style {a=1, b=2}, NOT the raw .NET Dictionary`2[...].
        assertEquals("{a=1, b=2}", mapOf("a" to 1, "b" to 2).toString())   // {a=1, b=2}
        val mm = mutableMapOf<String, Int>()
        mm["x"] = 9
        assertEquals("{x=9}", mm.toString())            // {x=9}
        assertEquals("[1, 2, 3]", listOf(1, 2, 3).toString())              // [1, 2, 3]
    }

    @TestAttribute
    fun mapvalues_mapValues() {
        // #29: groupBy().mapValues{} + a direct size/containsKey on a value-type-mismatched groupBy result.
        val counts = listOf(1, 2, 3, 4).groupBy { it % 2 }.mapValues { it.value.size }
        assertEquals("{1=2, 0=2}", counts.toString())   // {1=2, 0=2}

        val g = listOf(1, 2, 3, 4, 5).groupBy { it % 2 }
        assertEquals(2, g.size)                         // 2
        assertTrue(g.containsKey(1))                    // True
        assertFalse(g.containsKey(2))                   // False
        assertEquals("{1=9, 0=6}", g.mapValues { it.value.sum() }.toString())  // {1=9, 0=6}

        val words = listOf("apple", "avocado", "banana", "cherry")
        val byFirst = words.groupBy { it.first() }.mapValues { it.value.size }
        assertEquals("{a=2, b=1, c=1}", byFirst.toString())   // {a=2, b=1, c=1}

        val plain = mapOf("a" to 1, "b" to 2, "c" to 3)
        assertEquals(3, plain.size)                     // 3
        assertTrue(plain.containsKey("b"))              // True
        assertFalse(plain.containsKey("z"))             // False
        assertEquals("{a=10, b=20, c=30}", plain.mapValues { it.value * 10 }.toString())  // {a=10, b=20, c=30}
        val mm = mutableMapOf("x" to 1, "y" to 2)
        assertEquals(2, mm.size)                        // 2
        assertTrue(mm.containsKey("x"))                 // True
    }
}
