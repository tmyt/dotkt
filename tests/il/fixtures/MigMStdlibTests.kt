// Stdlib-mapping battery (batch MigM, from cases/m-b1..m-b13 minus the m-b5 precondition sliver) — the sole
// JVM-oracle-backed proof that Kotlin's collection / scope-function / kotlin.math / kotlin.text / bitwise stdlib
// maps to the correct CLR behavior. Migrated onto the in-process NUnit suite; each old case's `main` + JVM golden
// becomes one @TestAttribute method whose per-value assert is strictly stronger (typed) than the old stdout diff.
// EVERY asserted value is preserved 1:1 (see `// <expected>`) — these cases were the only oracle proof of these
// mappings, so no assertion is dropped.
//
// Coverage preserved (old case -> method):
//   m-b1  -> mapFilterForEach        map / filter / forEach with value-returning lambdas
//   m-b2  -> scopeFunctions          apply / also / let / run / with (receiver vs result, `this` vs `it`)
//   m-b3  -> foldAnyAllCountSumFirstTake  fold / any / all / count / sum / first / take
//   m-b4  -> mathAndStringOps        kotlin.math abs/max/sqrt -> System.Math; String uppercase/substring/replace/startsWith
//   m-b6  -> distinctSortedReduceMax distinct / sorted+joinToString / reduce / maxOrNull / setOf / joinToString
//   m-b7  -> splitAndMapOf           String.split / mapOf (Dictionary) lookup + size
//   m-b8  -> parseCharPredsCoerce    String.toInt / Char.isLetter/isDigit / coerceAtMost/coerceAtLeast/coerceIn
//   m-b9  -> firstLastNoneSumOfMaxBy firstOrNull / lastOrNull / none / sumOf / maxByOrNull
//   m-b10 -> groupByAssociate        groupBy / associateWith / associateBy (Dictionary)
//   m-b11 -> repeatReversed          String.repeat / reversed
//   m-b12 -> bitwiseAndShift         and / or / xor / shl / shr / inv
//   m-b13 -> stringUtilsAndIndex     isEmpty / isNotEmpty / isBlank / isNotBlank / char indexing
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.sqrt

// ---- m-b2 : scope functions operate on a mutable receiver ----------------------------------------------------
class MigMBox(var v: Int)

class MigMStdlibTests {
    // ---- m-b1 : map / filter / forEach ----
    @TestAttribute
    fun mapFilterForEach() {
        val xs = listOf(1, 2, 3, 4)
        val doubled = mutableListOf<Int>()
        xs.map { it * 2 }.forEach { doubled.add(it) }    // map -> [2,4,6,8], forEach appends in order
        assertEquals("2,4,6,8", doubled.joinToString(","))  // 2,4,6,8 (order preserved)
        assertEquals(2, xs.filter { it % 2 == 0 }.size)  // evens=2
    }

    // ---- m-b2 : apply / also / let / run / with ----
    @TestAttribute
    fun scopeFunctions() {
        val b = MigMBox(1).apply { v = 10 }   // apply: returns receiver; `this`.v
        assertEquals(10, b.v)                 // 10
        assertEquals(15, b.let { it.v + 5 })  // let: returns result; `it` -> 15
        assertEquals(20, with(b) { v * 2 })   // with: returns result; `this` -> 20
        val a = MigMBox(3).also { it.v = 7 }  // also: returns receiver; `it`
        assertEquals(7, a.v)                  // 7
        assertEquals(11, b.run { v + 1 })     // run: returns result; `this` -> 11
    }

    // ---- m-b3 : fold / any / all / count / sum / first / take ----
    @TestAttribute
    fun foldAnyAllCountSumFirstTake() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals(15, xs.fold(0) { acc, e -> acc + e })  // 15
        assertTrue(xs.any { it > 4 })                       // true
        assertTrue(xs.all { it > 0 })                       // true
        assertEquals(2, xs.count { it % 2 == 0 })           // 2
        assertEquals(15, xs.sum())                          // 15
        assertEquals(3, xs.first { it > 2 })                // 3
        assertEquals(2, xs.take(2).size)                    // 2
    }

    // ---- m-b4 : kotlin.math -> System.Math, kotlin.text String ops -> .NET String ----
    @TestAttribute
    fun mathAndStringOps() {
        assertEquals(5, abs(-5))                            // 5
        assertEquals(8, max(3, 8))                          // 8
        assertEquals(4.0, sqrt(16.0))                       // 4.0
        val s = "Hello, World"
        assertEquals("HELLO, WORLD", s.uppercase())         // HELLO, WORLD
        assertEquals("Hello", s.substring(0, 5))            // Hello
        assertEquals("Hello, CLR", s.replace("World", "CLR"))  // Hello, CLR
        assertTrue(s.startsWith("Hello"))                   // true
    }

    // ---- m-b6 : distinct / sorted / reduce / maxOrNull / setOf / joinToString ----
    @TestAttribute
    fun distinctSortedReduceMax() {
        val xs = listOf(3, 1, 2, 2, 4)
        assertEquals(4, xs.distinct().size)                 // 4
        assertEquals("1-2-2-3-4", xs.sorted().joinToString("-"))  // 1-2-2-3-4
        assertEquals(12, xs.reduce { a, b -> a + b })       // 12
        assertEquals(4, xs.maxOrNull())                     // 4
        assertEquals(3, setOf(1, 2, 2, 3).size)             // 3
        assertEquals("a, b, c", listOf("a", "b", "c").joinToString(", "))  // a, b, c
    }

    // ---- m-b7 : String.split + mapOf (Dictionary) ----
    @TestAttribute
    fun splitAndMapOf() {
        val parts = "a,b,c".split(",")
        assertEquals(3, parts.size)                         // 3
        assertEquals("a|b|c", parts.joinToString("|"))      // a|b|c
        val m = mapOf("x" to 1, "y" to 2)
        assertEquals(1, m["x"])                             // 1
        assertEquals(2, m.size)                             // 2
    }

    // ---- m-b8 : String->number parse, Char predicates, coerce* ----
    @TestAttribute
    fun parseCharPredsCoerce() {
        assertEquals(43, "42".toInt() + 1)                  // 43
        assertTrue('a'.isLetter())                          // true
        assertTrue('5'.isDigit())                           // true
        assertEquals(7, 10.coerceAtMost(7))                 // 7
        assertEquals(5, 3.coerceAtLeast(5))                 // 5
        assertEquals(5, 8.coerceIn(1, 5))                   // 5
    }

    // ---- m-b9 : firstOrNull / lastOrNull / none / sumOf / maxByOrNull ----
    @TestAttribute
    fun firstLastNoneSumOfMaxBy() {
        val xs = listOf(3, 1, 4, 1, 5)
        assertEquals(4, xs.firstOrNull { it > 3 })          // 4
        assertEquals(5, xs.lastOrNull())                    // 5
        assertTrue(xs.none { it > 10 })                     // true
        assertEquals(28, xs.sumOf { it * 2 })               // 28
        assertEquals(1, xs.maxByOrNull { -it })             // 1 (max of -it -> smallest element)
    }

    // ---- m-b10 : groupBy / associateWith / associateBy ----
    @TestAttribute
    fun groupByAssociate() {
        val xs = listOf(1, 2, 3, 4)
        assertEquals(2, xs.groupBy { it % 2 }.size)                 // 2 (keys {1,0})
        assertEquals(9, xs.associateWith { it * it }[3])           // 9
        assertEquals(3, listOf("a", "bb", "ccc").associateBy { it.length }.size)  // 3 (keys {1,2,3})
    }

    // ---- m-b11 : String.repeat / reversed ----
    @TestAttribute
    fun repeatReversed() {
        assertEquals("ababab", "ab".repeat(3))              // ababab
        assertEquals("olleh", "hello".reversed())           // olleh
    }

    // ---- m-b12 : bitwise / shift operators ----
    @TestAttribute
    fun bitwiseAndShift() {
        assertEquals(2, 6 and 3)                            // 2
        assertEquals(7, 6 or 3)                             // 7
        assertEquals(5, 6 xor 3)                            // 5
        assertEquals(16, 1 shl 4)                           // 16
        assertEquals(8, 32 shr 2)                           // 8
        assertEquals(-6, 5.inv())                           // -6
    }

    // ---- m-b13 : string utilities isEmpty/isNotEmpty/isBlank/isNotBlank + indexing ----
    @TestAttribute
    fun stringUtilsAndIndex() {
        assertTrue("".isEmpty())                            // true
        assertTrue("hi".isNotEmpty())                       // true
        assertTrue("  ".isBlank())                          // true
        assertTrue("abc".isNotBlank())                      // true
        assertEquals('b', "abc"[1])                         // b
    }
}
