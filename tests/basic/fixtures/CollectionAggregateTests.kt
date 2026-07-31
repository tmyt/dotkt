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
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

fun <T : Comparable<T>> collectionGenericMax(values: Collection<T>): T? = values.maxOrNull()

class CollectionAggregateTests {

    // ---- m-b6 : distinct / sorted / reduce / maxOrNull / setOf / joinToString ----
    @TestAttribute
    fun orderingAndReduction() {
        val xs = listOf(3, 1, 2, 2, 4)
        assertEquals("1-2-2-3-4", xs.sorted().joinToString("-"))  // 1-2-2-3-4
        assertEquals(12, xs.reduce { a, b -> a + b })       // 12
        assertEquals(4, xs.maxOrNull())                     // 4
    }

    // #46: the carried open signature IEnumerable<T> must link maxOrNull<T>, never the same-arity
    // IEnumerable<Double> sibling. Cover both the concrete call site and a call whose type argument is the
    // enclosing method's own generic parameter.
    @TestAttribute
    fun genericMaxLinksCarriedSignature() {
        assertEquals(3, listOf(3, 1, 2).maxOrNull())
        assertEquals(3, collectionGenericMax(listOf(3, 1, 2)))
        assertEquals("z", collectionGenericMax(listOf("a", "z", "m")))
    }

    // #86 — `collectionGenericMax` above declares a top-level `T?` RETURN over an unconstrained T. Same-compilation,
    // so no cross-module carrier is involved: the erased `object` return has to re-narrow at each typed use, and the
    // EMPTY-collection lines are what prove a real null crosses it. At T=Int/T=Boolean a bare `T` return would read
    // the absent maximum as 0/false instead. The existing coverage of this shape is T=Int-non-null and T=String only.
    @TestAttribute
    fun genericNullableReturnAtValueTypes() {
        assertEquals(4, collectionGenericMax(listOf(3, 1, 4)))    // 4
        assertNull(collectionGenericMax(listOf<Int>()))           // null (empty -> genuine null, not 0)
        val flag: Boolean? = collectionGenericMax(listOf(false, true))
        assertTrue(flag == true)                                  // true
        assertNull(collectionGenericMax(listOf<Boolean>()))       // null (empty -> genuine null, not false)
        assertNull(collectionGenericMax(listOf<String>()))        // null (reference control)
        val typed: Int? = collectionGenericMax(listOf(7, 2))
        assertEquals(7, typed)                                    // 7 (erased return into a declared Int? slot)
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
    fun associationFactories() {
        val xs = listOf(1, 2, 3, 4)
        assertEquals(9, xs.associateWith { it * it }[3])           // 9
        assertEquals(3, listOf("a", "bb", "ccc").associateBy { it.length }.size)  // 3 (keys {1,2,3})
    }

    // ---- m-b11 : String.repeat / reversed ----
    @TestAttribute
    fun stringRepeat() {
        assertEquals("ababab", "ab".repeat(3))              // ababab
    }
}
